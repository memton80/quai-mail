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
/// oubliées à la demande.
/// </summary>
public sealed partial class PgpKeysViewModel : ObservableObject
{
    private readonly PgpKeyStore _store;
    private readonly IFileDialogs _files;
    private readonly IStrings _strings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PgpKeysViewModel(PgpKeyStore store, IFileDialogs files, IStrings strings, TimeProvider? time = null, ILogger<PgpKeysViewModel>? logger = null)
    {
        _store = store;
        _files = files;
        _strings = strings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
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
    }
}

/// <summary>Une clé dans la liste des réglages.</summary>
/// <param name="RemoveMessage">Confirmation affichée avant de la retirer.</param>
public sealed record PgpKeyItem(PgpKeyInfo Key, string Details, string RemoveMessage)
{
    public string UserId => Key.UserId;

    public string Fingerprint => Key.DisplayFingerprint;
}
