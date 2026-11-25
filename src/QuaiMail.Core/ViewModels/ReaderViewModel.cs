using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Volet de lecture : le mail ouvert (en-têtes natifs, corps nettoyé dans la WebView2 verrouillée), ses pièces jointes,
/// lu / non lu, étoile. Images distantes bloquées sauf demande (« Afficher les images ») ou réglage contraire. Liens :
/// ouverts dans le navigateur seulement après confirmation, adresse réelle affichée.
/// </summary>
public sealed partial class ReaderViewModel : ObservableObject
{
    private readonly IMailService _mail;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogs _files;
    private readonly ILauncher _launcher;
    private readonly IStrings _strings;
    private readonly ReaderTemplate _template;
    private readonly ILogger _logger;
    private CancellationTokenSource? _loading;
    private Account? _account;
    private string? _folder;
    private MessageHeader? _header;
    private bool _imagesAllowed;

    public ReaderViewModel(
        IMailService mail,
        ISettingsService settings,
        IDialogService dialogs,
        IFileDialogs files,
        ILauncher launcher,
        IStrings strings,
        ReaderTemplate template,
        ILogger<ReaderViewModel>? logger = null)
    {
        _mail = mail;
        _settings = settings;
        _dialogs = dialogs;
        _files = files;
        _launcher = launcher;
        _strings = strings;
        _template = template;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Subject = string.Empty;
        From = string.Empty;
        To = string.Empty;
        Cc = string.Empty;
        DateText = string.Empty;
    }

    /// <summary>Une nouvelle page est prête (<see cref="Page"/>) : la vue la charge dans la WebView2.</summary>
    public event EventHandler? PageChanged;

    /// <summary>Lu / non lu ou étoile changé : la liste et le cache suivent.</summary>
    public event EventHandler<MessageHeader>? HeaderChanged;

    /// <summary>Mail affiché (pour servir ses images jointes à la WebView2).</summary>
    public MessageContent? Content { get; private set; }

    /// <summary>Page HTML complète à afficher (gabarit, politique CSP, corps nettoyé).</summary>
    public string? Page { get; private set; }

    /// <summary>Numéro de la page affichée : change à chaque nouvelle page (adresse locale unique).</summary>
    public int PageNumber { get; private set; }

    public ObservableCollection<AttachmentInfo> Attachments { get; } = [];

    [ObservableProperty]
    public partial bool HasMessage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; }

    [ObservableProperty]
    public partial string From { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCc))]
    public partial string To { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCc))]
    public partial string Cc { get; set; }

    public bool HasCc => !string.IsNullOrEmpty(Cc);

    [ObservableProperty]
    public partial string DateText { get; set; }

    [ObservableProperty]
    public partial bool IsRead { get; set; }

    [ObservableProperty]
    public partial bool IsFlagged { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersImages))]
    public partial int BlockedImages { get; set; }

    /// <summary>Les images distantes de ce mail sont affichées (demandées, ou réglage « Bloquer » désactivé).</summary>
    public bool RemoteImagesShown => _imagesAllowed && Content is not null;

    /// <summary>Des images distantes ont été bloquées : bandeau « Afficher les images ».</summary>
    public bool OffersImages => BlockedImages > 0;

    [ObservableProperty]
    public partial bool HasAttachments { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Ouvre un mail : en-têtes tout de suite, puis le contenu ; marqué lu sur le serveur s'il ne l'était pas.</summary>
    public async Task ShowAsync(Account account, string folder, MessageHeader header)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var cancellation = _loading = new CancellationTokenSource();
        _account = account;
        _folder = folder;
        _header = header;
        _imagesAllowed = !_settings.Current.BlockRemoteImages;
        Content = null;
        HasMessage = true;
        ErrorMessage = null;
        Subject = string.IsNullOrWhiteSpace(header.Subject) ? _strings.Get("Draft_NoSubject") : header.Subject;
        From = string.IsNullOrWhiteSpace(header.FromName) ? header.FromAddress : $"{header.FromName} <{header.FromAddress}>";
        To = string.Empty;
        Cc = string.Empty;
        DateText = header.Date == DateTimeOffset.MinValue ? string.Empty : header.Date.ToLocalTime().ToString("f", System.Globalization.CultureInfo.CurrentCulture);
        IsRead = header.IsRead;
        IsFlagged = header.IsFlagged;
        BlockedImages = 0;
        Attachments.Clear();
        HasAttachments = false;
        SetPage(string.Empty);

        IsLoading = true;
        try
        {
            var content = await _mail.GetMessageAsync(account, folder, header.Uid, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Content = content;
            To = content.To;
            Cc = content.Cc;
            foreach (var attachment in content.Attachments)
            {
                Attachments.Add(attachment);
            }

            HasAttachments = Attachments.Count > 0;
            Render();
        }
        catch (MailConnectionException ex) when (!cancellation.IsCancellationRequested)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
            return;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }

        if (!header.IsRead)
        {
            await SetFlagsAsync(seen: true, flagged: null);
        }
    }

    /// <summary>« Afficher les images » : ce mail-ci seulement, ses images distantes chargées.</summary>
    [RelayCommand]
    public void ShowImages()
    {
        if (Content is null || _imagesAllowed)
        {
            return;
        }

        _imagesAllowed = true;
        Render();
    }

    [RelayCommand]
    public Task ToggleReadAsync() => SetFlagsAsync(seen: !IsRead, flagged: null);

    [RelayCommand]
    public Task ToggleFlagAsync() => SetFlagsAsync(seen: null, flagged: !IsFlagged);

    /// <summary>
    /// Un lien du mail : seulement http, https et mailto, et seulement après confirmation montrant l'adresse réelle
    /// (pas le texte du lien, qui peut mentir).
    /// </summary>
    public async Task<bool> OpenLinkAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            _logger.LogInformation("Lien refusé (schéma {Scheme})", uri.IsAbsoluteUri ? uri.Scheme : "relatif");
            return false;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            _strings.Get(uri.Scheme == "mailto" ? "Link_MailTitle" : "Link_Title"),
            _strings.Format("Link_Message", uri.AbsoluteUri, uri.IsDefaultPort || uri.Scheme == "mailto" ? uri.Host : $"{uri.Host}:{uri.Port}"),
            _strings.Get("Link_Open"));
        return confirmed && _launcher.Open(uri);
    }

    /// <summary>
    /// Enregistre une pièce jointe là où l'utilisateur le choisit (écriture d'un bloc : jamais de fichier à moitié écrit).
    /// Jamais ouverte automatiquement ; un type exécutable est signalé avant.
    /// </summary>
    public async Task<string?> SaveAttachmentAsync(AttachmentInfo attachment)
    {
        if (Content is not { } content)
        {
            return null;
        }

        if (attachment.IsExecutable
            && !await _dialogs.ConfirmAsync(
                _strings.Get("Attachment_DangerTitle"),
                _strings.Format("Attachment_DangerMessage", attachment.FileName),
                _strings.Get("Attachment_SaveAnyway"),
                isDestructive: true))
        {
            return null;
        }

        if (await _files.PickSaveLocationAsync(attachment.FileName) is not { } path)
        {
            return null;
        }

        try
        {
            await AtomicFile.WriteAsync(path, (stream, ct) => content.WriteAttachmentAsync(attachment.Index, stream, ct));
            _logger.LogInformation("Pièce jointe enregistrée ({Size} octets)", attachment.Size);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Pièce jointe non enregistrée ({Error})", ex.GetType().Name);
            await _dialogs.ShowErrorAsync(_strings.Get("Attachment_SaveFailedTitle"), _strings.Get("Attachment_SaveFailedMessage"));
            return null;
        }
    }

    /// <summary>Plus aucun mail ouvert (autre dossier, compte retiré).</summary>
    public void Clear()
    {
        _loading?.Cancel();
        _account = null;
        _folder = null;
        _header = null;
        Content = null;
        HasMessage = false;
        IsLoading = false;
        ErrorMessage = null;
        BlockedImages = 0;
        Attachments.Clear();
        HasAttachments = false;
        SetPage(string.Empty);
    }

    private void Render()
    {
        if (Content is not { } content)
        {
            return;
        }

        var body = content.BodyHtml(_imagesAllowed);
        BlockedImages = body.BlockedImages;
        try
        {
            SetPage(ReaderPage.Build(_template.Html, body.Html, _imagesAllowed));
        }
        catch (InvalidOperationException ex)
        {
            // Gabarit inattendu : rien n'est affiché plutôt qu'un mail sans protection.
            _logger.LogError(ex, "Gabarit de lecture inutilisable");
            ErrorMessage = _strings.Get("Reader_TemplateError");
            SetPage(string.Empty);
        }
    }

    private void SetPage(string page)
    {
        Page = page;
        PageNumber++;
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SetFlagsAsync(bool? seen, bool? flagged)
    {
        if (_account is not { } account || _folder is not { } folder || _header is not { } header)
        {
            return;
        }

        try
        {
            await _mail.SetFlagsAsync(account, folder, header.Uid, seen, flagged);
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
            return;
        }

        if (_header?.Uid != header.Uid)
        {
            return;
        }

        var updated = header with { IsRead = seen ?? header.IsRead, IsFlagged = flagged ?? header.IsFlagged };
        _header = updated;
        IsRead = updated.IsRead;
        IsFlagged = updated.IsFlagged;
        HeaderChanged?.Invoke(this, updated);
    }
}
