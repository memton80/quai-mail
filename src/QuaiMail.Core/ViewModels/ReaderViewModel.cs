using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Safety;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Volet de lecture : le mail ouvert (en-têtes natifs, corps nettoyé dans la WebView2 verrouillée), ses pièces jointes,
/// lu / non lu, étoile. Images distantes bloquées sauf demande (« Afficher les images ») ou réglage contraire. Liens :
/// ouverts dans le navigateur seulement après confirmation, adresse réelle affichée. Expéditeur sûr (liste locale) :
/// affichage normal, images comprises ; sinon mode méfiant (bandeau, indices de hameçonnage, liens trompeurs signalés),
/// et un échec des contrôles d'authentification suspend la confiance.
/// </summary>
public sealed partial class ReaderViewModel : ObservableObject
{
    private readonly IMailService _mail;
    private readonly Cache.MailCache? _cache;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogs _files;
    private readonly ILauncher _launcher;
    private readonly IStrings _strings;
    private readonly ReaderTemplate _template;
    private readonly ILogger _logger;
    private readonly IAttachmentSafety? _safety;
    private readonly TrustedSenders? _trusted;
    private readonly SignatureChecker? _signatures;
    private IReadOnlySet<string> _deceptiveHosts = new HashSet<string>();
    private CancellationTokenSource? _loading;
    private Account? _account;
    private string? _folder;
    private MessageHeader? _header;
    private bool _imagesAllowed;
    private MessageContent? _content;

    public ReaderViewModel(
        IMailService mail,
        ISettingsService settings,
        IDialogService dialogs,
        IFileDialogs files,
        ILauncher launcher,
        IStrings strings,
        ReaderTemplate template,
        ILogger<ReaderViewModel>? logger = null,
        Cache.MailCache? cache = null,
        IAttachmentSafety? safety = null,
        TrustedSenders? trusted = null,
        SignatureChecker? signatures = null)
    {
        _mail = mail;
        _signatures = signatures;
        _safety = safety;
        _trusted = trusted;
        _cache = cache;
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
        SafetyTitle = string.Empty;
        SafetyMessage = string.Empty;
        TrustAddressText = string.Empty;
        TrustDomainText = string.Empty;
        SignatureTitle = string.Empty;
        SignatureMessage = string.Empty;
        LookUpKeyText = string.Empty;
        if (trusted is not null)
        {
            trusted.Changed += (_, _) => OnTrustChanged();
        }
    }

    /// <summary>Une nouvelle page est prête (<see cref="Page"/>) : la vue la charge dans la WebView2.</summary>
    public event EventHandler? PageChanged;

    /// <summary>Lu / non lu ou étoile changé : la liste et le cache suivent.</summary>
    public event EventHandler<MessageChange>? HeaderChanged;

    /// <summary>Compte du mail affiché (pour y répondre).</summary>
    public Account? Account => _account;

    /// <summary>Dossier du mail affiché.</summary>
    public string? Folder => _folder;

    /// <summary>UID du mail affiché.</summary>
    public uint? Uid => _header?.Uid;

    /// <summary>Mail affiché (pour servir ses images jointes à la WebView2, et y répondre).</summary>
    public MessageContent? Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

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

    /// <summary>Confiance dans l'expéditeur du mail affiché.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSafetyNotice))]
    public partial SenderStatus Sender { get; set; }

    /// <summary>Mode méfiant : bandeau en tête du mail.</summary>
    public bool HasSafetyNotice => Sender is SenderStatus.Unknown or SenderStatus.Suspicious or SenderStatus.Spoofed;

    [ObservableProperty]
    public partial string SafetyTitle { get; set; }

    /// <summary>Pourquoi se méfier, une raison par ligne.</summary>
    [ObservableProperty]
    public partial string SafetyMessage { get; set; }

    /// <summary>« Faire confiance » proposé (jamais quand l'authentification a échoué).</summary>
    [ObservableProperty]
    public partial bool CanTrustSender { get; set; }

    /// <summary>« Tout le domaine » proposé (jamais pour une messagerie ouverte à tous).</summary>
    [ObservableProperty]
    public partial bool CanTrustDomain { get; set; }

    [ObservableProperty]
    public partial string TrustAddressText { get; set; }

    [ObservableProperty]
    public partial string TrustDomainText { get; set; }

    /// <summary>Mail signé en OpenPGP : bandeau de la signature (vérification, verdict, clé trouvée ou inconnue).</summary>
    [ObservableProperty]
    public partial bool HasSignature { get; set; }

    /// <summary>Dernier verdict (<c>null</c> pendant la vérification).</summary>
    [ObservableProperty]
    public partial SignatureReport? Signature { get; set; }

    [ObservableProperty]
    public partial SignatureLevel SignatureLevel { get; set; }

    [ObservableProperty]
    public partial string SignatureTitle { get; set; }

    [ObservableProperty]
    public partial string SignatureMessage { get; set; }

    /// <summary>« Chercher sur keys.openpgp.org » (recherche automatique désactivée) ou « Réessayer » (pas de réponse).</summary>
    [ObservableProperty]
    public partial bool CanLookUpKey { get; set; }

    [ObservableProperty]
    public partial string LookUpKeyText { get; set; }

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
        ResetSender();
        ResetSignature();
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

            // Le mail complet fait foi (la liste n'a que l'enveloppe).
            if (!string.IsNullOrWhiteSpace(content.Subject))
            {
                Subject = content.Subject;
            }

            if (!string.IsNullOrWhiteSpace(content.FromAddress))
            {
                From = content.From;
            }

            To = content.To;
            Cc = content.Cc;
            foreach (var attachment in content.Attachments)
            {
                Attachments.Add(attachment);
            }

            HasAttachments = Attachments.Count > 0;
            EvaluateSender();
            Render();

            // Signature OpenPGP : vérifiée à part (la clé peut devoir être demandée au serveur de clés).
            if (content.IsPgpSigned && _signatures is not null)
            {
                _ = CheckSignatureAsync(content, _settings.Current.LookUpPgpKeys, cancellation.Token);
            }

            // Texte gardé en cache pour la recherche plein texte (sur cet ordinateur seulement).
            _cache?.IndexBody(account.Id, folder, header.Uid, $"{content.From} {content.To} {content.Cc}\n{Compose.ReplyBuilder.PlainText(content)}");
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

    /// <summary>« Faire confiance à cette adresse » : ses mails s'affichent normalement, images comprises.</summary>
    [RelayCommand]
    public void TrustSender()
    {
        if (CanTrustSender && Content is { } content && _trusted?.TrustAddress(content.FromAddress) == true)
        {
            _logger.LogInformation("Expéditeur ajouté aux expéditeurs sûrs");
        }
    }

    /// <summary>« Faire confiance à tout le domaine » (sauf messagerie ouverte à tous).</summary>
    [RelayCommand]
    public void TrustDomain()
    {
        if (CanTrustDomain && Content is { } content && MailAddressRules.TryGetDomain(content.FromAddress, out var domain)
            && _trusted?.TrustDomain(domain) == true)
        {
            _logger.LogInformation("Domaine ajouté aux expéditeurs sûrs");
        }
    }

    [RelayCommand]
    public Task ToggleReadAsync() => SetFlagsAsync(seen: !IsRead, flagged: null);

    [RelayCommand]
    public Task ToggleFlagAsync() => SetFlagsAsync(seen: null, flagged: !IsFlagged);

    /// <summary>
    /// Un lien du mail : seulement http, https et mailto, et seulement après confirmation montrant l'adresse réelle
    /// (pas le texte du lien, qui peut mentir). Lien dont le texte annonçait une autre adresse, ou expéditeur pas sûr :
    /// la confirmation le dit.
    /// </summary>
    public async Task<bool> OpenLinkAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            _logger.LogInformation("Lien refusé (schéma {Scheme})", uri.IsAbsoluteUri ? uri.Scheme : "relatif");
            return false;
        }

        var message = _strings.Format("Link_Message", uri.AbsoluteUri, uri.IsDefaultPort || uri.Scheme == "mailto" ? uri.Host : $"{uri.Host}:{uri.Port}");
        var deceptive = uri.Scheme != "mailto" && _deceptiveHosts.Contains(uri.IdnHost);
        if (deceptive)
        {
            message = _strings.Get("Link_Deceptive") + "\n\n" + message;
        }
        else if (HasSafetyNotice)
        {
            message += "\n\n" + _strings.Get("Link_UnknownSender");
        }

        var confirmed = await _dialogs.ConfirmAsync(
            _strings.Get(uri.Scheme == "mailto" ? "Link_MailTitle" : "Link_Title"),
            message,
            _strings.Get("Link_Open"),
            isDestructive: deceptive);
        return confirmed && _launcher.Open(uri);
    }

    /// <summary>
    /// Enregistre une pièce jointe là où l'utilisateur le choisit (écriture d'un bloc : jamais de fichier à moitié écrit),
    /// puis la fait marquer « venue d'Internet » par Windows (antivirus, SmartScreen à l'ouverture). Jamais ouverte
    /// automatiquement ; un type exécutable est signalé avant ; un fichier bloqué par Windows est retiré.
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

        // Le sélecteur de Windows peut échouer (COMException) : jamais en silence, l'utilisateur doit le savoir.
        string? path;
        try
        {
            path = await _files.PickSaveLocationAsync(attachment.FileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Sélecteur d'enregistrement en échec ({Error}, 0x{Code:X8})", ex.GetType().Name, ex.HResult);
            await _dialogs.ShowErrorAsync(_strings.Get("Attachment_SaveFailedTitle"), _strings.Get("Attachment_PickerFailedMessage"));
            return null;
        }

        if (path is null)
        {
            return null;
        }

        try
        {
            await AtomicFile.WriteAsync(path, (stream, ct) => content.WriteAttachmentAsync(attachment.Index, stream, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Pièce jointe non enregistrée ({Error}, 0x{Code:X8})", ex.GetType().Name, ex.HResult);
            await _dialogs.ShowErrorAsync(_strings.Get("Attachment_SaveFailedTitle"), _strings.Get("Attachment_SaveFailedMessage"));
            return null;
        }

        var verdict = _safety is null ? SavedFileVerdict.Unmarked : await _safety.MarkAsync(path);
        if (verdict == SavedFileVerdict.Blocked)
        {
            _logger.LogWarning("Pièce jointe bloquée par Windows (antivirus ou stratégie de sécurité), fichier retiré");
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Pièce jointe bloquée impossible à retirer ({Error})", ex.GetType().Name);
            }

            await _dialogs.ShowErrorAsync(_strings.Get("Attachment_BlockedTitle"), _strings.Get("Attachment_BlockedMessage"));
            return null;
        }

        if (verdict == SavedFileVerdict.Unmarked)
        {
            _logger.LogWarning("Pièce jointe enregistrée sans marque du web (volume sans flux NTFS ?)");
        }

        _logger.LogInformation("Pièce jointe enregistrée ({Size} octets)", attachment.Size);
        return path;
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
        ResetSender();
        ResetSignature();
        SetPage(string.Empty);
    }

    /// <summary>« Chercher sur keys.openpgp.org » / « Réessayer » : la clé inconnue est demandée au serveur de clés.</summary>
    [RelayCommand]
    public Task LookUpKeyAsync() =>
        Content is { IsPgpSigned: true } content && _loading is { } loading
            ? CheckSignatureAsync(content, useKeyServer: true, loading.Token)
            : Task.CompletedTask;

    private async Task CheckSignatureAsync(MessageContent content, bool useKeyServer, CancellationToken cancellationToken)
    {
        HasSignature = true;
        Signature = null;
        SignatureLevel = SignatureLevel.Checking;
        SignatureTitle = _strings.Get("Signature_Title");
        SignatureMessage = _strings.Get("Signature_Checking");
        CanLookUpKey = false;
        try
        {
            var report = await _signatures!.CheckAsync(content, useKeyServer, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(Content, content))
            {
                ShowSignature(report, content);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Autre mail ouvert entre-temps.
        }
    }

    private void ShowSignature(SignatureReport report, MessageContent content)
    {
        var server = _signatures?.KeyServerName ?? string.Empty;
        var who = report.SignerUserId ?? _strings.Get("Signature_NoIdentity");
        var fingerprint = report.DisplayFingerprint;
        (SignatureLevel, SignatureTitle, SignatureMessage) = report.State switch
        {
            SignatureState.Valid => (SignatureLevel.Good, _strings.Get("Signature_ValidTitle"), _strings.Format(
                report.Source switch
                {
                    SignatureKeySource.OwnKey => "Signature_ValidOwn",
                    SignatureKeySource.Kleopatra => "Signature_ValidKleopatra",
                    _ => "Signature_ValidServer",
                },
                who,
                server,
                fingerprint)),
            SignatureState.OtherIdentity => (SignatureLevel.Warning, _strings.Get("Signature_OtherIdentityTitle"), _strings.Format("Signature_OtherIdentity", who, content.FromAddress, fingerprint)),
            SignatureState.KeyNotValid => (SignatureLevel.Warning, _strings.Get("Signature_KeyNotValidTitle"), _strings.Format("Signature_KeyNotValid", who, fingerprint)),
            SignatureState.Invalid => (SignatureLevel.Bad, _strings.Get("Signature_InvalidTitle"), _strings.Get("Signature_Invalid")),
            _ => (SignatureLevel.Warning, _strings.Get("Signature_UnknownTitle"), _strings.Format(
                report.ServerUnreachable ? "Signature_UnknownUnreachable" : report.LookedUp || server.Length == 0 ? "Signature_UnknownNotFound" : "Signature_Unknown",
                fingerprint,
                server)),
        };
        CanLookUpKey = report.State == SignatureState.UnknownKey && server.Length > 0 && (!report.LookedUp || report.ServerUnreachable);
        LookUpKeyText = report.ServerUnreachable ? _strings.Get("Signature_Retry") : _strings.Format("Signature_LookUp", server);
        Signature = report;
    }

    private void ResetSignature()
    {
        HasSignature = false;
        Signature = null;
        CanLookUpKey = false;
        SignatureMessage = string.Empty;
        SignatureTitle = string.Empty;
    }

    /// <summary>
    /// Expéditeur sûr (liste, ou l'adresse du compte lui-même) et aucun échec d'authentification : affichage normal,
    /// images comprises. Sinon mode méfiant : bandeau, raisons de se méfier, liens trompeurs notés pour la confirmation.
    /// </summary>
    private void EvaluateSender()
    {
        if (Content is not { } content)
        {
            ResetSender();
            return;
        }

        var address = content.FromAddress;
        var normalized = TrustedSenders.NormalizeAddress(address);
        var own = normalized is not null && _account is { } account && normalized == TrustedSenders.NormalizeAddress(account.Address);
        var trusted = own || (_trusted?.IsTrusted(address) ?? false);
        var failed = content.AuthenticationFailed;
        if (trusted && !failed)
        {
            ResetSender();
            Sender = SenderStatus.Trusted;
            _imagesAllowed = true;
            return;
        }

        var warnings = MessageSafety.Analyze(content.FromName, address, content.ReplyToAddresses.Select(a => a.Address), failed, content.Links, KnownDomains());
        _deceptiveHosts = MessageSafety.DeceptiveHosts(content.Links);
        var shown = string.IsNullOrWhiteSpace(address) ? _strings.Get("Safety_NoAddress") : address;
        var lines = new List<string> { _strings.Format(trusted ? "Safety_TrustedFailed" : "Safety_Unknown", shown) };
        lines.AddRange(warnings.Select(w => "• " + Describe(w)));
        SafetyMessage = string.Join("\n", lines);
        Sender = failed ? SenderStatus.Spoofed : warnings.Count > 0 ? SenderStatus.Suspicious : SenderStatus.Unknown;
        SafetyTitle = _strings.Get(Sender switch
        {
            SenderStatus.Spoofed => "Safety_SpoofedTitle",
            SenderStatus.Suspicious => "Safety_SuspiciousTitle",
            _ => "Safety_UnknownTitle",
        });

        var domain = normalized?[(normalized.LastIndexOf('@') + 1)..];
        CanTrustSender = !trusted && !failed && normalized is not null && _trusted is not null;
        CanTrustDomain = CanTrustSender && TrustedSenders.CanTrustDomain(domain);
        TrustAddressText = _strings.Format("Safety_TrustAddress", normalized ?? string.Empty);
        TrustDomainText = _strings.Format("Safety_TrustDomain", domain ?? string.Empty);
    }

    private void ResetSender()
    {
        Sender = SenderStatus.None;
        SafetyTitle = string.Empty;
        SafetyMessage = string.Empty;
        CanTrustSender = false;
        CanTrustDomain = false;
        _deceptiveHosts = new HashSet<string>();
    }

    /// <summary>Liste des expéditeurs sûrs changée (ici ou dans les Réglages) : le mail affiché est réévalué.</summary>
    private void OnTrustChanged()
    {
        if (Content is null)
        {
            return;
        }

        var imagesBefore = _imagesAllowed;
        EvaluateSender();
        if (_imagesAllowed != imagesBefore)
        {
            Render();
        }
    }

    /// <summary>Domaines connus : un domaine qui leur ressemble sans être le même est signalé.</summary>
    private List<string> KnownDomains()
    {
        var domains = new List<string>(KnownProviders.PublicDomains);
        if (_trusted is not null)
        {
            domains.AddRange(_trusted.Domains);
            domains.AddRange(_trusted.Addresses.Select(a => a[(a.LastIndexOf('@') + 1)..]));
        }

        if (_account is { } account && MailAddressRules.TryGetDomain(account.Address, out var own))
        {
            domains.Add(own);
        }

        return domains;
    }

    private string Describe(SafetyWarning warning) => warning.Kind switch
    {
        SafetyWarningKind.AuthenticationFailed => _strings.Format("Safety_AuthenticationFailed", warning.First),
        SafetyWarningKind.NameShowsOtherAddress => _strings.Format("Safety_NameShowsOtherAddress", warning.First, warning.Second ?? string.Empty),
        SafetyWarningKind.ReplyToElsewhere => _strings.Format("Safety_ReplyToElsewhere", warning.First),
        SafetyWarningKind.LookalikeDomain => _strings.Format("Safety_LookalikeDomain", warning.First, warning.Second ?? string.Empty),
        SafetyWarningKind.InternationalDomain => _strings.Format("Safety_InternationalDomain", warning.First, warning.Second ?? string.Empty),
        SafetyWarningKind.DeceptiveLink => _strings.Format("Safety_DeceptiveLink", warning.First, warning.Second ?? string.Empty),
        SafetyWarningKind.NumericLink => _strings.Format("Safety_NumericLink", warning.First),
        _ => string.Empty,
    };

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

    /// <summary>
    /// Lu / non lu ou étoile changés hors du volet de lecture (clic droit sur une sélection) : le mail affiché suit,
    /// sans rien redemander au serveur.
    /// </summary>
    public void SyncHeader(string accountId, string folder, MessageHeader header)
    {
        if (_header is not { } shown || shown.Uid != header.Uid || _account?.Id != accountId || _folder != folder)
        {
            return;
        }

        _header = shown with { IsRead = header.IsRead, IsFlagged = header.IsFlagged };
        IsRead = header.IsRead;
        IsFlagged = header.IsFlagged;
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
        HeaderChanged?.Invoke(this, new MessageChange(account.Id, folder, updated));
    }
}

/// <summary>Un mail dont lu / non lu ou l'étoile a changé.</summary>
public sealed record MessageChange(string AccountId, string Folder, MessageHeader Header);

/// <summary>Couleur du bandeau de la signature.</summary>
public enum SignatureLevel
{
    /// <summary>Vérification en cours (clé cherchée).</summary>
    Checking,

    /// <summary>Signature valide, clé de l'expéditeur.</summary>
    Good,

    /// <summary>Clé inconnue, autre identité, clé expirée ou révoquée.</summary>
    Warning,

    /// <summary>Signature fausse : le mail a été modifié.</summary>
    Bad,
}
