using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.Safety;

/// <summary>Contenu de <c>trusted-senders.json</c>.</summary>
public sealed class TrustedSenderList
{
    /// <summary>Adresses sûres, en minuscules, domaine en ASCII.</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>Domaines sûrs (sous-domaines compris), en minuscules et en ASCII.</summary>
    public List<string> Domains { get; set; } = [];
}

/// <summary>
/// Expéditeurs sûrs (<c>%LOCALAPPDATA%\QuaiMail\trusted-senders.json</c>), sur cet ordinateur seulement : une adresse,
/// ou tout un domaine (jamais celui d'une messagerie publique comme gmail.com, où n'importe qui crée une adresse). Leurs
/// mails s'affichent normalement, images comprises ; les autres passent en mode méfiant. Une adresse sûre ne protège
/// pas d'une usurpation : un échec d'authentification (DMARC, SPF, DKIM) suspend la confiance pour ce mail.
/// </summary>
public sealed class TrustedSenders
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private TrustedSenderList? _list;

    public TrustedSenders(AppPaths paths, ILogger<TrustedSenders>? logger = null)
        : this(paths.TrustedSendersFile, logger)
    {
    }

    public TrustedSenders(string path, ILogger<TrustedSenders>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<TrustedSenders>.Instance;
    }

    /// <summary>Déclenché après chaque ajout ou retrait enregistré.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> Addresses
    {
        get
        {
            lock (_gate)
            {
                return [.. Load().Addresses];
            }
        }
    }

    public IReadOnlyList<string> Domains
    {
        get
        {
            lock (_gate)
            {
                return [.. Load().Domains];
            }
        }
    }

    /// <summary>L'adresse, ou son domaine (ou un domaine parent), est dans la liste.</summary>
    public bool IsTrusted(string? address)
    {
        if (NormalizeAddress(address) is not { } normalized)
        {
            return false;
        }

        var domain = normalized[(normalized.LastIndexOf('@') + 1)..];
        lock (_gate)
        {
            var list = Load();
            return list.Addresses.Contains(normalized, StringComparer.Ordinal)
                || list.Domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));
        }
    }

    /// <summary>Un domaine entier peut être déclaré sûr, sauf celui d'une messagerie ouverte à tous.</summary>
    public static bool CanTrustDomain(string? domain) =>
        MailAddressRules.TryNormalizeHost(domain, out var host) && host.Contains('.') && !KnownProviders.IsPublicDomain(host);

    /// <returns><c>false</c> si l'adresse est invalide ou déjà dans la liste.</returns>
    public bool TrustAddress(string? address)
    {
        if (NormalizeAddress(address) is not { } normalized)
        {
            return false;
        }

        return Change(list =>
        {
            if (list.Addresses.Contains(normalized, StringComparer.Ordinal))
            {
                return false;
            }

            list.Addresses.Add(normalized);
            list.Addresses.Sort(StringComparer.Ordinal);
            return true;
        });
    }

    /// <returns><c>false</c> si le domaine est invalide, public (gmail.com…) ou déjà dans la liste.</returns>
    public bool TrustDomain(string? domain)
    {
        if (!CanTrustDomain(domain) || !MailAddressRules.TryNormalizeHost(domain, out var host))
        {
            return false;
        }

        return Change(list =>
        {
            if (list.Domains.Contains(host, StringComparer.Ordinal))
            {
                return false;
            }

            list.Domains.Add(host);
            list.Domains.Sort(StringComparer.Ordinal);
            return true;
        });
    }

    /// <summary>Retire une adresse ou un domaine de la liste.</summary>
    public bool Remove(string entry) =>
        Change(list => list.Addresses.Remove(entry) | list.Domains.Remove(entry));

    /// <summary>Adresse comparable : minuscules, domaine en ASCII (noms internationaux), <c>null</c> si invalide.</summary>
    public static string? NormalizeAddress(string? address)
    {
        if (!MailAddressRules.TryGetDomain(address, out var domain))
        {
            return null;
        }

        var trimmed = address!.Trim();
        return trimmed[..trimmed.LastIndexOf('@')].ToLowerInvariant() + "@" + domain;
    }

    private bool Change(Func<TrustedSenderList, bool> change)
    {
        lock (_gate)
        {
            var current = Load();
            var updated = new TrustedSenderList { Addresses = [.. current.Addresses], Domains = [.. current.Domains] };
            if (!change(updated))
            {
                return false;
            }

            // Écriture d'abord : en cas d'échec, la liste en mémoire reste celle du disque.
            JsonFile.Write(_path, updated, QuaiMailJsonContext.Default.TrustedSenderList);
            _list = updated;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private TrustedSenderList Load()
    {
        if (_list is not null)
        {
            return _list;
        }

        try
        {
            var read = JsonFile.Read(_path, QuaiMailJsonContext.Default.TrustedSenderList) ?? new TrustedSenderList();
            _list = new TrustedSenderList
            {
                Addresses = [.. (read.Addresses ?? []).Select(NormalizeAddress).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                Domains = [.. (read.Domains ?? []).Where(CanTrustDomain).Select(d => MailAddressRules.TryNormalizeHost(d, out var h) ? h : null).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Liste des expéditeurs sûrs illisible ({Error}), mise de côté", ex.GetType().Name);
            try
            {
                JsonFile.Quarantine(_path);
            }
            catch (Exception quarantine) when (quarantine is IOException or UnauthorizedAccessException)
            {
                // Le fichier reste en place ; il sera remplacé au prochain enregistrement.
            }

            _list = new TrustedSenderList();
        }

        return _list;
    }
}
