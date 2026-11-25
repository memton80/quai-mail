using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Compose;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Services;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Rédaction d'un message (nouveau, réponse, réponse à tous, transfert). Le brouillon ouvert est celui de
/// <see cref="ShellViewModel.CurrentDraft"/> : enregistré en local toutes les quelques secondes
/// (<see cref="AutoSaveAsync"/>), puis copié dans le dossier Brouillons du serveur. <b>Envoyé uniquement par
/// <see cref="SendCommand"/></b>, c'est-à-dire le bouton Envoyer : rien d'autre n'envoie de mail.
/// </summary>
public sealed partial class ComposerViewModel : ObservableObject
{
    /// <summary>Délai minimal entre deux copies du brouillon sur le serveur.</summary>
    public static readonly TimeSpan ServerSyncInterval = TimeSpan.FromSeconds(30);

    private readonly AccountStore _accounts;
    private readonly DraftStore _drafts;
    private readonly IMailService _mail;
    private readonly ShellViewModel _shell;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogs _files;
    private readonly IStrings _strings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private bool _loading;
    private bool _dirty;
    private bool _needsServerSync;
    private DateTimeOffset _lastServerSync = DateTimeOffset.MinValue;
    private Task? _sync;

    public ComposerViewModel(
        AccountStore accounts,
        DraftStore drafts,
        IMailService mail,
        ShellViewModel shell,
        IDialogService dialogs,
        IFileDialogs files,
        IStrings strings,
        TimeProvider? time = null,
        ILogger<ComposerViewModel>? logger = null)
    {
        _accounts = accounts;
        _drafts = drafts;
        _mail = mail;
        _shell = shell;
        _dialogs = dialogs;
        _files = files;
        _strings = strings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Attachments.CollectionChanged += (_, _) => HasAttachments = Attachments.Count > 0;
        To = string.Empty;
        Cc = string.Empty;
        Bcc = string.Empty;
        Subject = string.Empty;
        Body = string.Empty;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentDraft))
            {
                Load(_shell.CurrentDraft);
            }
        };
        Load(_shell.CurrentDraft);
    }

    /// <summary>Comptes d'envoi possibles.</summary>
    public ObservableCollection<Account> Accounts { get; } = [];

    public ObservableCollection<DraftAttachment> Attachments { get; } = [];

    [ObservableProperty]
    public partial bool HasAttachments { get; set; }

    public Draft? Draft { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>Compte d'envoi (index dans <see cref="Accounts"/>).</summary>
    [ObservableProperty]
    public partial int AccountIndex { get; set; }

    [ObservableProperty]
    public partial string To { get; set; }

    [ObservableProperty]
    public partial string Cc { get; set; }

    [ObservableProperty]
    public partial string Bcc { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; }

    [ObservableProperty]
    public partial string Body { get; set; }

    /// <summary>Champs Cc et Cci affichés.</summary>
    [ObservableProperty]
    public partial bool ShowCopies { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>« Brouillon enregistré à 10:42 » (et dans Brouillons sur le serveur).</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Dernier envoi réussi, pour le bandeau de confirmation de la fenêtre.</summary>
    [ObservableProperty]
    public partial string? SentMessage { get; set; }

    /// <summary>Bandeau « Mail envoyé » affiché (le fermer l'efface).</summary>
    [ObservableProperty]
    public partial bool ShowSent { get; set; }

    /// <summary>Nouveau message, depuis ce compte (sinon le premier).</summary>
    public async Task<bool> NewAsync(string? accountId)
    {
        if (!await CloseCurrentAsync())
        {
            return false;
        }

        var account = _accounts.Find(accountId) ?? _accounts.All.FirstOrDefault();
        _shell.OpenDraft(new Draft { AccountId = account?.Id });
        return true;
    }

    /// <summary>Réponse (ou réponse à tous) au mail lu.</summary>
    public async Task<bool> ReplyAsync(MessageContent original, Account account, bool all)
    {
        if (!await CloseCurrentAsync())
        {
            return false;
        }

        var draft = ReplyBuilder.Reply(original, account, all);
        _shell.OpenDraft(draft);
        MarkDirty();
        SaveLocal();
        return true;
    }

    /// <summary>Transfert du mail lu, pièces jointes comprises (copiées dans le brouillon).</summary>
    public async Task<bool> ForwardAsync(MessageContent original, Account account)
    {
        if (!await CloseCurrentAsync())
        {
            return false;
        }

        var draft = ReplyBuilder.Forward(original, account);
        foreach (var attachment in original.Attachments)
        {
            await _drafts.AddAttachmentAsync(draft, attachment.FileName, (stream, ct) => original.WriteAttachmentAsync(attachment.Index, stream, ct));
        }

        _shell.OpenDraft(draft);
        MarkDirty();
        SaveLocal();
        return true;
    }

    /// <summary>
    /// Appelé toutes les quelques secondes par la fenêtre : enregistrement local s'il y a du nouveau, puis copie sur le
    /// serveur au plus toutes les <see cref="ServerSyncInterval"/>.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (IsSending || _sync is { IsCompleted: false })
        {
            return;
        }

        SaveLocal();
        if (_needsServerSync && _time.GetUtcNow() - _lastServerSync >= ServerSyncInterval)
        {
            await SyncServerAsync();
        }
    }

    /// <summary>Enregistre le brouillon sur cet ordinateur (s'il a changé).</summary>
    public void SaveLocal()
    {
        if (Draft is not { } draft || !_dirty || draft.IsEmpty)
        {
            return;
        }

        try
        {
            _drafts.Save(draft);
            _dirty = false;
            _needsServerSync = true;
            Status = _strings.Format("Composer_SavedLocal", Clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Brouillon {Id} non enregistré ({Error})", draft.Id, ex.GetType().Name);
            Status = _strings.Get("Composer_SaveFailed");
        }
    }

    /// <summary>Copie le brouillon dans le dossier Brouillons du serveur (remplace la copie précédente).</summary>
    public Task SyncServerAsync()
    {
        // Une seule copie à la fois : l'envoi et la fermeture attendent celle en cours.
        if (_sync is { IsCompleted: false } running)
        {
            return running;
        }

        _sync = SyncServerCoreAsync();
        return _sync;
    }

    private async Task SyncServerCoreAsync()
    {
        if (Draft is not { } draft || draft.IsEmpty || _accounts.Find(draft.AccountId) is not { } account)
        {
            return;
        }

        _lastServerSync = _time.GetUtcNow();
        try
        {
            var message = MessageComposer.Build(account, draft, _drafts, forSending: false);
            var previous = draft.ServerFolder is { } folder ? new ServerCopy(folder, draft.ServerUid) : null;
            var copy = await _mail.SaveDraftAsync(account, message, previous);
            if (Draft?.Id != draft.Id)
            {
                return;
            }

            draft.ServerFolder = copy?.Folder;
            draft.ServerUid = copy?.Uid;
            _drafts.Save(draft);
            _needsServerSync = false;
            Status = copy is null ? _strings.Format("Composer_SavedLocal", Clock()) : _strings.Format("Composer_SavedServer", Clock());
        }
        catch (MailConnectionException ex)
        {
            // Hors connexion : le brouillon reste en local, la copie sera refaite plus tard.
            _logger.LogInformation("Brouillon {Id} non copié sur le serveur ({Error})", draft.Id, ex.Error);
            Status = _strings.Format("Composer_SavedLocalOnly", Clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Brouillon {Id} : copie serveur interrompue ({Error})", draft.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// <b>Le seul chemin d'envoi</b> (bouton Envoyer) : destinataires vérifiés, envoi SMTP, copie dans Envoyés, puis le
    /// brouillon est retiré (local et serveur). En cas d'échec, rien n'est perdu : le brouillon reste ouvert.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    public async Task SendAsync()
    {
        if (Draft is not { } draft)
        {
            return;
        }

        ErrorMessage = null;
        MarkDirty();
        SaveLocal();
        if (_sync is { } pending)
        {
            await pending;
        }

        if (_accounts.Find(draft.AccountId) is not { } account)
        {
            ErrorMessage = _strings.Get("Composer_NoAccount");
            return;
        }

        MimeKit.MimeMessage message;
        try
        {
            message = MessageComposer.Build(account, draft, _drafts, forSending: true);
        }
        catch (InvalidRecipientsException ex)
        {
            ErrorMessage = ex.Invalid.Count == 0
                ? _strings.Get("Composer_NoRecipient")
                : _strings.Format("Composer_InvalidRecipients", string.Join(", ", ex.Invalid));
            return;
        }

        IsSending = true;
        SendOutcome outcome;
        try
        {
            outcome = await _mail.SendAsync(account, message);
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
            return;
        }
        finally
        {
            IsSending = false;
        }

        _logger.LogInformation("Brouillon {Id} envoyé", draft.Id);
        await DeleteServerCopyAsync(account, draft);
        try
        {
            _drafts.Delete(draft.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Brouillon {Id} envoyé mais non effacé ({Error})", draft.Id, ex.GetType().Name);
        }

        SentMessage = outcome.SentCopy is null && !outcome.ServerKeepsCopy
            ? _strings.Get("Composer_SentNoCopy")
            : _strings.Get("Composer_Sent");
        _shell.CurrentDraft = null;
    }

    /// <summary>
    /// Fermer le message : « Garder ce brouillon ? » (Garder : en local et dans Brouillons ; Supprimer : effacé partout ;
    /// Annuler : il reste ouvert).
    /// </summary>
    [RelayCommand]
    public async Task CloseAsync() => await CloseCurrentAsync();

    /// <summary>Joindre des fichiers (copiés dans le brouillon).</summary>
    [RelayCommand]
    public async Task AttachAsync()
    {
        if (Draft is not { } draft)
        {
            return;
        }

        foreach (var path in await _files.PickFilesToAttachAsync())
        {
            try
            {
                var attachment = await _drafts.AddAttachmentAsync(draft, Path.GetFileName(path), async (stream, ct) =>
                {
                    await using var source = File.OpenRead(path);
                    await source.CopyToAsync(stream, ct);
                });
                Attachments.Add(attachment);
                MarkDirty();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Fichier non joint ({Error})", ex.GetType().Name);
                ErrorMessage = _strings.Format("Composer_AttachFailed", Path.GetFileName(path));
            }
        }

        SaveLocal();
    }

    public void RemoveAttachment(DraftAttachment attachment)
    {
        if (Draft is not { } draft)
        {
            return;
        }

        _drafts.RemoveAttachment(draft, attachment);
        Attachments.Remove(attachment);
        MarkDirty();
        SaveLocal();
    }

    partial void OnSentMessageChanged(string? value) => ShowSent = value is not null;

    partial void OnShowSentChanged(bool value)
    {
        if (!value)
        {
            SentMessage = null;
        }
    }

    partial void OnAccountIndexChanged(int value)
    {
        if (!_loading && Draft is { } draft && value >= 0 && value < Accounts.Count)
        {
            draft.AccountId = Accounts[value].Id;
            MarkDirty();
        }
    }

    partial void OnToChanged(string value) => Edit(d => d.To = value);

    partial void OnCcChanged(string value) => Edit(d => d.Cc = value);

    partial void OnBccChanged(string value) => Edit(d => d.Bcc = value);

    partial void OnSubjectChanged(string value) => Edit(d => d.Subject = value);

    partial void OnBodyChanged(string value) => Edit(d => d.Body = value);

    private bool CanSend() => !IsSending;

    private void Edit(Action<Draft> change)
    {
        if (_loading || Draft is not { } draft)
        {
            return;
        }

        change(draft);
        MarkDirty();
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>Ferme le brouillon ouvert (question « Garder ce brouillon ? ») ; <c>false</c> si l'utilisateur annule.</summary>
    private async Task<bool> CloseCurrentAsync()
    {
        if (Draft is not { } draft)
        {
            return true;
        }

        SaveLocal();
        if (_sync is { } pending)
        {
            await pending;
        }

        var account = _accounts.Find(draft.AccountId);
        var copy = draft.ServerFolder;
        if (!await _shell.ConfirmCloseDraftAsync())
        {
            return false;
        }

        if (_shell.CurrentDraft is null)
        {
            // Supprimé (ou vide) : la copie du serveur part aussi.
            if (account is not null && copy is not null)
            {
                await DeleteServerCopyAsync(account, draft);
            }
        }
        else
        {
            // Gardé : aussi dans Brouillons du serveur, puis fermé.
            _needsServerSync = true;
            await SyncServerAsync();
            _shell.CurrentDraft = null;
        }

        return true;
    }

    private async Task DeleteServerCopyAsync(Account account, Draft draft)
    {
        if (draft.ServerFolder is not { } folder)
        {
            return;
        }

        try
        {
            await _mail.DeleteDraftAsync(account, new ServerCopy(folder, draft.ServerUid));
        }
        catch (MailConnectionException ex)
        {
            _logger.LogInformation("Copie serveur du brouillon {Id} non retirée ({Error})", draft.Id, ex.Error);
        }
    }

    private void Load(Draft? draft)
    {
        _loading = true;
        try
        {
            Draft = draft;
            IsOpen = draft is not null;
            ErrorMessage = null;
            Accounts.Clear();
            foreach (var account in _accounts.All)
            {
                Accounts.Add(account);
            }

            Attachments.Clear();
            if (draft is null)
            {
                To = Cc = Bcc = Subject = Body = string.Empty;
                Status = null;
                _dirty = false;
                _needsServerSync = false;
                return;
            }

            if (draft.AccountId is null && Accounts.Count > 0)
            {
                draft.AccountId = Accounts[0].Id;
            }

            AccountIndex = Math.Max(0, Accounts.ToList().FindIndex(a => a.Id == draft.AccountId));
            To = draft.To;
            Cc = draft.Cc;
            Bcc = draft.Bcc;
            Subject = draft.Subject;
            Body = draft.Body;
            ShowCopies = !string.IsNullOrEmpty(draft.Cc) || !string.IsNullOrEmpty(draft.Bcc);
            foreach (var attachment in draft.Attachments)
            {
                Attachments.Add(attachment);
            }

            Status = null;
            _dirty = false;
            _lastServerSync = DateTimeOffset.MinValue;
        }
        finally
        {
            _loading = false;
        }
    }

    private string Clock() => _time.GetLocalNow().ToString("t", CultureInfo.CurrentCulture);
}
