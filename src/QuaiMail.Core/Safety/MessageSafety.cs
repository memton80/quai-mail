using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using MimeKit;
using MimeKit.Cryptography;
using QuaiMail.Core.Accounts;

namespace QuaiMail.Core.Safety;

/// <summary>Ce qui rend un mail suspect (mode méfiant).</summary>
public enum SafetyWarningKind
{
    /// <summary>Le serveur de réception n'a pas pu confirmer l'expéditeur (DMARC, ou SPF sans DKIM valable, en échec).</summary>
    AuthenticationFailed,

    /// <summary>Le nom affiché contient une autre adresse que celle de l'expéditeur.</summary>
    NameShowsOtherAddress,

    /// <summary>Les réponses partiraient vers un autre domaine que celui de l'expéditeur.</summary>
    ReplyToElsewhere,

    /// <summary>Le domaine de l'expéditeur ressemble à un domaine connu sans être le même.</summary>
    LookalikeDomain,

    /// <summary>Le domaine de l'expéditeur est écrit en caractères internationaux (sosie possible d'un nom latin).</summary>
    InternationalDomain,

    /// <summary>Le texte d'un lien annonce une adresse, le lien en mène à une autre.</summary>
    DeceptiveLink,

    /// <summary>Un lien mène à une adresse IP plutôt qu'à un nom de site.</summary>
    NumericLink,
}

/// <param name="First">Premier détail (adresse, domaine…), affiché tel quel.</param>
/// <param name="Second">Second détail, s'il y en a un.</param>
public sealed record SafetyWarning(SafetyWarningKind Kind, string First, string? Second = null);

/// <summary>Un lien du mail : son texte visible et l'adresse où il mène.</summary>
public sealed record MailLink(string Text, Uri Target);

/// <summary>
/// Indices de hameçonnage lus dans le mail lui-même, sans rien demander à personne : contrôles d'authentification notés
/// par le serveur de réception, nom affiché, adresse de réponse, domaine sosie, liens trompeurs.
/// </summary>
public static partial class MessageSafety
{
    private const int MaxLinkWarnings = 3;

    /// <summary>Fins de nom qui sont surtout des types de fichier : « facture.pdf » n'annonce pas un site.</summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "csv", "rtf", "jpg", "jpeg", "png",
        "gif", "webp", "htm", "html", "php", "asp", "aspx", "jsp", "exe", "msi", "zip", "rar", "mov", "mp3", "mp4", "ics",
        "vcf", "eml", "xml", "json",
    };

    /// <summary>
    /// Résultat des contrôles d'authentification noté par le serveur de réception : le premier en-tête
    /// <c>Authentication-Results</c> (le plus récent, ajouté par ce serveur). Seul un échec compte : un « pass » écrit
    /// par l'expéditeur lui-même plus bas dans le mail ne peut donc rien obtenir.
    /// </summary>
    /// <returns><c>true</c> si DMARC échoue, ou si SPF échoue sans aucune signature DKIM valable.</returns>
    public static bool AuthenticationFailed(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var header = message.Headers.FirstOrDefault(h => h.Id == HeaderId.AuthenticationResults);
        if (header is null || !AuthenticationResults.TryParse(header.RawValue, out var results))
        {
            return false;
        }

        bool Has(string method, string result) => results.Results.Any(r =>
            string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Result, result, StringComparison.OrdinalIgnoreCase));

        if (results.Results.Any(r => string.Equals(r.Method, "dmarc", StringComparison.OrdinalIgnoreCase)))
        {
            return Has("dmarc", "fail");
        }

        return Has("spf", "fail") && !Has("dkim", "pass");
    }

    /// <summary>Liens http et https du corps HTML, avec leur texte visible (le corps texte n'a pas de texte trompeur).</summary>
    public static IReadOnlyList<MailLink> Links(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var document = new HtmlParser().ParseDocument(html);
        var links = new List<MailLink>();
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (Uri.TryCreate(anchor.GetAttribute("href")?.Trim(), UriKind.Absolute, out var target) && target.Scheme is "http" or "https")
            {
                links.Add(new MailLink(Collapse(anchor.TextContent), target));
            }
        }

        return links;
    }

    /// <summary>Avertissements pour un mail dont l'expéditeur n'est pas sûr.</summary>
    /// <param name="knownDomains">Domaines connus (sûrs, du compte, grandes messageries) : un sosie de l'un d'eux est signalé.</param>
    public static IReadOnlyList<SafetyWarning> Analyze(
        string fromName,
        string fromAddress,
        IEnumerable<string> replyTo,
        bool authenticationFailed,
        IReadOnlyList<MailLink> links,
        IEnumerable<string> knownDomains)
    {
        var warnings = new List<SafetyWarning>();
        MailAddressRules.TryGetDomain(fromAddress, out var domain);

        if (authenticationFailed)
        {
            warnings.Add(new SafetyWarning(SafetyWarningKind.AuthenticationFailed, string.IsNullOrEmpty(domain) ? fromAddress : domain));
        }

        foreach (Match match in AddressPattern().Matches(fromName ?? string.Empty))
        {
            if (!string.Equals(match.Value, fromAddress, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new SafetyWarning(SafetyWarningKind.NameShowsOtherAddress, match.Value, fromAddress));
                break;
            }
        }

        foreach (var address in replyTo)
        {
            if (MailAddressRules.TryGetDomain(address, out var replyDomain) && domain.Length > 0 && !SameSite(replyDomain, domain))
            {
                warnings.Add(new SafetyWarning(SafetyWarningKind.ReplyToElsewhere, address));
                break;
            }
        }

        if (domain.Length > 0)
        {
            var known = knownDomains.Select(d => d.ToLowerInvariant()).Distinct().ToList();
            if (!known.Any(k => SameSite(domain, k)) && known.FirstOrDefault(k => LooksLike(domain, k)) is { } original)
            {
                warnings.Add(new SafetyWarning(SafetyWarningKind.LookalikeDomain, domain, original));
            }

            if (domain.Split('.').Any(label => label.StartsWith("xn--", StringComparison.Ordinal)))
            {
                warnings.Add(new SafetyWarning(SafetyWarningKind.InternationalDomain, Unicode(domain), domain));
            }
        }

        foreach (var link in DeceptiveLinks(links).Take(MaxLinkWarnings))
        {
            warnings.Add(link);
        }

        return warnings;
    }

    /// <summary>Hôtes réels des liens dont le texte annonce une autre adresse (confirmation renforcée au clic).</summary>
    public static IReadOnlySet<string> DeceptiveHosts(IReadOnlyList<MailLink> links) =>
        DeceptiveLinks(links).Where(w => w.Kind == SafetyWarningKind.DeceptiveLink).Select(w => w.Second!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Distance d'édition (Levenshtein) : nombre minimal de lettres ajoutées, retirées ou changées.</summary>
    public static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>Sosie : même « squelette » (0/o, 1/l, rn/m, vv/w…) ou une ou deux lettres de différence.</summary>
    internal static bool LooksLike(string domain, string known)
    {
        if (string.Equals(domain, known, StringComparison.Ordinal) || known.Length < 5)
        {
            return false;
        }

        if (Skeleton(domain) == Skeleton(known))
        {
            return true;
        }

        var distance = Distance(domain, known);
        return distance <= (known.Length >= 10 ? 2 : 1);
    }

    /// <summary>Même site : même domaine, ou l'un est un sous-domaine de l'autre.</summary>
    internal static bool SameSite(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)
        || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SafetyWarning> DeceptiveLinks(IReadOnlyList<MailLink> links)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            var host = link.Target.IdnHost.ToLowerInvariant();
            if (link.Target.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(host, out _))
            {
                if (seen.Add("ip:" + host))
                {
                    yield return new SafetyWarning(SafetyWarningKind.NumericLink, host);
                }

                continue;
            }

            if (ShownHost(link.Text) is { } shown && !SameSite(WithoutWww(shown), WithoutWww(host)) && seen.Add(shown + ">" + host))
            {
                yield return new SafetyWarning(SafetyWarningKind.DeceptiveLink, shown, host);
            }
        }
    }

    /// <summary>Nom de site annoncé par le texte d'un lien (« https://www.banque.example/compte », « banque.example »), ou rien.</summary>
    private static string? ShownHost(string text)
    {
        if (text.Length is 0 or > 300 || text.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.IdnHost.ToLowerInvariant();
        }

        var match = ShownDomainPattern().Match(text);
        if (!match.Success || !MailAddressRules.TryNormalizeHost(match.Groups["host"].Value, out var host))
        {
            return null;
        }

        return !host.StartsWith("www.", StringComparison.Ordinal) && FileExtensions.Contains(host[(host.LastIndexOf('.') + 1)..]) ? null : host;
    }

    private static string WithoutWww(string host) => host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

    private static string Skeleton(string domain) =>
        domain.ToLowerInvariant().Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal)
            .Replace('0', 'o').Replace('1', 'l').Replace('3', 'e').Replace('5', 's').Replace('i', 'l');

    private static string Unicode(string domain)
    {
        try
        {
            return new IdnMapping().GetUnicode(domain);
        }
        catch (ArgumentException)
        {
            return domain;
        }
    }

    private static string Collapse(string text) => WhitespacePattern().Replace(text, " ").Trim();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^(?<host>(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,})(?:[/?#].*)?$")]
    private static partial Regex ShownDomainPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
