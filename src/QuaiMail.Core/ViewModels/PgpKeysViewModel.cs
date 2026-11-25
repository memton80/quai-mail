using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.Services;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Réglages, « Signature des mails (OpenPGP) » : clés secrètes importées depuis GnuPG ou Kleopatra, retirées après
/// confirmation (dans la vue : bouton « Retirer » puis « Retirer » du message de confirmation), phrases de passe
/// oubliées à la demande. Si GnuPG (Kleopatra) est installé, ses clés de signature sont listées à chaque ouverture des
/// Réglages et se copient dans Quai Mail en un clic : la copie reste dans Quai Mail, GnuPG n'est jamais modifié.
/// </summary>
public sealed partial class PgpKeysViewModel : ObservableObject
{
    private readonly PgpKeyStore _store;
    private readonly IFileDialogs _files;
    private readonly IStrings _strings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IGnuPgLocator? _locator;
    private IGnuPg? _gnupg;
    private IReadOnlyList<GnuPgKey> _found = [];

    public PgpKeysViewModel(
        PgpKeyStore store,
        IFileDialogs files,
        IStrings strings,
        TimeProvider? time = null,
        ILogger<PgpKeysViewModel>? logger = null,
        IGnuPgLocator? gnupg = null)
    {
        _store = store;
        _files = files;
        _strings = strings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _locator = gnupg;
        _store.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<PgpKeyItem> Keys { get; } = [];

    [ObservableProperty]
    public partial bool HasKeys { get; set; }

    /// <summary>Résultat du dernier import (ou erreur).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    public bool HasMessage => Message is not null;

    /// <summary>Clés de signature trouvées dans GnuPG (Kleopatra).</summary>
    public ObservableCollection<GnuPgKeyItem> GnuPgKeys { get; } = [];

    /// <summary>GnuPG est installé : sa partie des réglages s'affiche.</summary>
    [ObservableProperty]
    public partial bool HasGnuPg { get; set; }

    [ObservableProperty]
    public partial bool HasGnuPgKeys { get; set; }

    /// <summary>« Kleopatra (Gpg4win) est installé » ou « GnuPG est installé ».</summary>
    [ObservableProperty]
    public partial string GnuPgTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDetecting { get; set; }

    /// <summary>
    /// Cherche GnuPG et ses clés secrètes capables de signer (à chaque ouverture des Réglages : une clé créée entre-temps
    /// dans Kleopatra apparaît). Lire la liste ne demande aucune phrase de passe et ne copie rien.
    /// </summary>
    public async Task DetectAsync()
    {
        if (_locator is null || IsDetecting)
        {
            return;
        }

        IsDetecting = true;
        try
        {
            var gnupg = await Task.Run(_locator.Find);
            _gnupg = gnupg;
            GnuPgTitle = gnupg is null ? string.Empty : _strings.Get(gnupg.HasKleopatra ? "Pgp_FoundKleopatra" : "Pgp_FoundGnuPg");
            _found = gnupg is null ? [] : await Task.Run(() => gnupg.ListSecretKeysAsync());
            HasGnuPg = gnupg is not null;
        }
        catch (GnuPgException ex)
        {
            _logger.LogWarning("Liste des clés de GnuPG illisible ({Error})", ex.Error);
            _found = [];
            HasGnuPg = _gnupg is not null;
            Show(_strings.Get("GnuPgError_" + ex.Error), error: true);
        }
        finally
        {
            IsDetecting = false;
        }

        RefreshGnuPg();
    }

    /// <summary>
    /// Copie une clé de GnuPG dans le trousseau de Quai Mail, où elle reste (même si Kleopatra est désinstallé) ; GnuPG
    /// n'est pas modifié. La phrase de passe est celle de la clé dans Kleopatra : elle ne sert qu'à cet export, n'est ni
    /// gardée ni écrite, et la clé copiée reste protégée par elle. Vide, GnuPG essaie sans (clé sans phrase de passe,
    /// ou phrase encore en mémoire chez GnuPG).
    /// </summary>
    /// <returns><c>true</c> si la clé est maintenant dans Quai Mail.</returns>
    public async Task<bool> ImportFromGnuPgAsync(GnuPgKeyItem item, string? passphrase)
    {
        if (_gnupg is not { } gnupg || !item.CanImport)
        {
            return false;
        }

        try
        {
            var armored = await Task.Run(() => gnupg.ExportSecretKeyAsync(item.Key.Fingerprint, string.IsNullOrEmpty(passphrase) ? null : passphrase));
            var imported = _store.Import(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(armored)));
            Show(_strings.Format("Pgp_Imported", string.Join(", ", imported.Select(k => k.UserId))), error: false);
            return true;
        }
        catch (GnuPgException ex)
        {
            if (ex.Error == GnuPgError.Failed)
            {
                _logger.LogWarning("Export de GnuPG refusé ({Error})", ex.Error);
            }

            Show(_strings.Get("GnuPgError_" + ex.Error), error: true);
        }
        catch (PgpKeyException ex)
        {
            Show(_strings.Get("PgpError_" + ex.Error), error: true);
        }

        return false;
    }

    /// <summary>« Importer une clé… » : fichier exporté de GnuPG ou de Kleopatra.</summary>
    public async Task ImportAsync()
    {
        if (await _files.PickKeyFileAsync() is not { } path)
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var imported = _store.Import(stream);
            Show(_strings.Format("Pgp_Imported", string.Join(", ", imported.Select(k => k.UserId))), error: false);
        }
        catch (PgpKeyException ex)
        {
            Show(_strings.Get("PgpError_" + ex.Error), error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Clé OpenPGP illisible ({Error})", ex.GetType().Name);
            Show(_strings.Get("PgpError_InvalidFile"), error: true);
        }
    }

    /// <summary>
    /// Retire la clé de Quai Mail (l'original reste dans GnuPG ou Kleopatra). Appelé seulement après la confirmation
    /// affichée par la vue (<see cref="PgpKeyItem.RemoveMessage"/>).
    /// </summary>
    public bool Remove(PgpKeyInfo key)
    {
        var removed = _store.Remove(key.Fingerprint);
        Message = null;
        return removed;
    }

    /// <summary>Oublie les phrases de passe gardées en mémoire depuis le démarrage.</summary>
    public void ForgetPassphrases()
    {
        _store.ForgetPassphrases();
        Show(_strings.Get("Pgp_Forgotten"), error: false);
    }

    private void Show(string message, bool error)
    {
        IsError = error;
        Message = message;
    }

    private void Refresh()
    {
        Keys.Clear();
        var now = _time.GetUtcNow();
        foreach (var key in _store.Keys)
        {
            var expiry = key.Expires is { } end
                ? _strings.Format(key.IsExpired(now) ? "Pgp_Expired" : "Pgp_Expires", end.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))
                : _strings.Get("Pgp_NoExpiry");
            Keys.Add(new PgpKeyItem(key, $"{key.Algorithm} · {expiry}", _strings.Format("Pgp_RemoveMessage", key.UserId, key.DisplayFingerprint)));
        }

        HasKeys = Keys.Count > 0;
        RefreshGnuPg();
    }

    /// <summary>
    /// État de chaque clé de GnuPG face au trousseau de Quai Mail : à importer, déjà copiée, copiée mais changée depuis
    /// (date d'expiration prolongée dans Kleopatra : « Mettre à jour »), ou sur carte à puce (impossible à copier).
    /// </summary>
    private void RefreshGnuPg()
    {
        GnuPgKeys.Clear();
        var tool = _strings.Get(_gnupg?.HasKleopatra == true ? "Pgp_Kleopatra" : "Pgp_GnuPg");
        foreach (var key in _found)
        {
            var copy = _store.Keys.FirstOrDefault(k => string.Equals(k.Fingerprint, key.Fingerprint, StringComparison.OrdinalIgnoreCase));
            var expiry = Expiry(key.Expires);
            GnuPgKeys.Add(key switch
            {
                { OnCard: true } => new GnuPgKeyItem(key, _strings.Get("Pgp_GnuPgOnCard"), false, string.Empty, string.Empty),
                _ when copy is null => new GnuPgKeyItem(
                    key, expiry, true, _strings.Get("Pgp_GnuPgImport"), _strings.Format("Pgp_GnuPgPassphrase", key.UserId, tool)),
                _ when copy.Expires != key.Expires => new GnuPgKeyItem(
                    key, _strings.Format("Pgp_GnuPgChanged", expiry, tool), true, _strings.Get("Pgp_GnuPgUpdate"), _strings.Format("Pgp_GnuPgPassphrase", key.UserId, tool)),
                _ => new GnuPgKeyItem(key, _strings.Get("Pgp_GnuPgAlready"), false, string.Empty, string.Empty),
            });
        }

        HasGnuPgKeys = GnuPgKeys.Count > 0;
    }

    private string Expiry(DateTimeOffset? expires) => expires is { } end
        ? _strings.Format("Pgp_Expires", end.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))
        : _strings.Get("Pgp_NoExpiry");
}

/// <summary>Une clé de GnuPG (Kleopatra) dans les réglages.</summary>
/// <param name="Status">État face à Quai Mail (à importer, déjà copiée, changée, sur carte à puce).</param>
/// <param name="CanImport">Bouton « Importer » (ou « Mettre à jour ») proposé.</param>
/// <param name="PassphrasePrompt">Texte au-dessus du champ de la phrase de passe.</param>
public sealed record GnuPgKeyItem(GnuPgKey Key, string Status, bool CanImport, string ActionText, string PassphrasePrompt)
{
    public string UserId => Key.UserId;

    public string Fingerprint => Key.DisplayFingerprint;
}

/// <summary>Une clé dans la liste des réglages.</summary>
/// <param name="RemoveMessage">Confirmation affichée avant de la retirer.</param>
public sealed record PgpKeyItem(PgpKeyInfo Key, string Details, string RemoveMessage)
{
    public string UserId => Key.UserId;

    public string Fingerprint => Key.DisplayFingerprint;
}
