using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Ajout d'un compte : adresse et mot de passe, réglages détectés (fournisseur connu ou autoconfiguration publiée par
/// le fournisseur), modifiables à la main, vérifiés par une vraie connexion IMAP et SMTP (aucun mail envoyé) avant
/// d'être enregistrés. Le mot de passe va dans le coffre de Windows, jamais dans un fichier.
/// </summary>
public sealed partial class AccountEditorViewModel : ObservableObject
{
    private readonly MailSettingsDiscovery _discovery;
    private readonly IMailService _mail;
    private readonly AccountStore _accounts;
    private readonly ICredentialStore _credentials;
    private readonly IStrings _strings;
    private readonly ILogger _logger;
    private string? _detectedFor;

    public AccountEditorViewModel(
        MailSettingsDiscovery discovery,
        IMailService mail,
        AccountStore accounts,
        ICredentialStore credentials,
        IStrings strings,
        ILogger<AccountEditorViewModel>? logger = null)
    {
        _discovery = discovery;
        _mail = mail;
        _accounts = accounts;
        _credentials = credentials;
        _strings = strings;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Address = string.Empty;
        DisplayName = string.Empty;
        Password = string.Empty;
        UserName = string.Empty;
        ImapHost = string.Empty;
        SmtpHost = string.Empty;
        ImapPort = 993;
        SmtpPort = 465;
    }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>Mot de passe (ou mot de passe d'application) ; effacé de la mémoire du formulaire une fois enregistré.</summary>
    [ObservableProperty]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; }

    [ObservableProperty]
    public partial string ImapHost { get; set; }

    [ObservableProperty]
    public partial double ImapPort { get; set; }

    /// <summary>0 = TLS dès la connexion, 1 = STARTTLS.</summary>
    [ObservableProperty]
    public partial int ImapSecurityIndex { get; set; }

    [ObservableProperty]
    public partial string SmtpHost { get; set; }

    [ObservableProperty]
    public partial double SmtpPort { get; set; }

    [ObservableProperty]
    public partial int SmtpSecurityIndex { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Réglages du serveur affichés (détectés, ou à saisir).</summary>
    [ObservableProperty]
    public partial bool ShowServerSettings { get; set; }

    /// <summary>D'où viennent les réglages, et ce qu'il faut savoir du fournisseur (mot de passe d'application…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string? Hint { get; set; }

    public bool HasHint => Hint is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Compte enregistré par le dernier <see cref="SaveAsync"/> réussi.</summary>
    public Account? SavedAccount { get; private set; }

    /// <summary>Cherche les réglages de l'adresse saisie (une fois par adresse).</summary>
    public async Task DetectAsync(CancellationToken cancellationToken = default)
    {
        var address = Address.Trim();
        if (!MailAddressRules.IsValidAddress(address))
        {
            ErrorMessage = _strings.Get("AccountEditor_InvalidAddress");
            return;
        }

        if (_detectedFor == address)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            if (await _discovery.DiscoverAsync(address, cancellationToken) is { } settings)
            {
                Apply(settings);
                _detectedFor = address;
            }
        }
        finally
        {
            IsBusy = false;
            ShowServerSettings = true;
        }
    }

    /// <summary>
    /// Vérifie les réglages par une vraie connexion IMAP et SMTP, puis enregistre le compte (et son mot de passe dans le
    /// coffre). Rien n'est enregistré si la vérification échoue.
    /// </summary>
    /// <returns>Le compte enregistré, ou <c>null</c> (<see cref="ErrorMessage"/> dit pourquoi).</returns>
    public async Task<Account?> SaveAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var address = Address.Trim();
        if (!MailAddressRules.IsValidAddress(address))
        {
            ErrorMessage = _strings.Get("AccountEditor_InvalidAddress");
            return null;
        }

        if (string.IsNullOrEmpty(Password))
        {
            ErrorMessage = _strings.Get("AccountEditor_MissingPassword");
            return null;
        }

        if (_detectedFor != address && string.IsNullOrWhiteSpace(ImapHost))
        {
            await DetectAsync(cancellationToken);
        }

        if (BuildAccount(address) is not { } account)
        {
            ShowServerSettings = true;
            ErrorMessage = _strings.Get("AccountEditor_InvalidServer");
            return null;
        }

        if (_accounts.All.Any(a => string.Equals(a.Address, account.Address, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(a.Imap.Host, account.Imap.Host, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = _strings.Get("AccountEditor_AlreadyAdded");
            return null;
        }

        IsBusy = true;
        try
        {
            await _mail.VerifyAsync(account, Password, cancellationToken);
            _credentials.SetPassword(account.Id, Password);
            _accounts.Save(account);
            Password = string.Empty;
            SavedAccount = account;
            _logger.LogInformation("Compte {Id} ajouté", account.Id);
            return account;
        }
        catch (MailConnectionException ex)
        {
            ShowServerSettings = true;
            ErrorMessage = MailErrors.Describe(_strings, ex);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnAddressChanged(string value)
    {
        // Identifiant suivi de l'adresse tant qu'il n'a pas été changé à la main.
        if (string.IsNullOrWhiteSpace(UserName) || _detectedFor is not null && UserName == _detectedFor)
        {
            UserName = value.Trim();
        }
    }

    private void Apply(MailSettings settings)
    {
        ImapHost = settings.Imap.Host;
        ImapPort = settings.Imap.Port;
        ImapSecurityIndex = settings.Imap.Security == MailSecurity.StartTls ? 1 : 0;
        SmtpHost = settings.Smtp.Host;
        SmtpPort = settings.Smtp.Port;
        SmtpSecurityIndex = settings.Smtp.Security == MailSecurity.StartTls ? 1 : 0;
        UserName = settings.UserName;

        var source = settings.Source switch
        {
            MailSettingsSource.KnownProvider => _strings.Format("AccountEditor_SourceKnown", settings.ProviderName ?? string.Empty),
            MailSettingsSource.ProviderAutoconfig => _strings.Get("AccountEditor_SourceAutoconfig"),
            _ => _strings.Get("AccountEditor_SourceGuess"),
        };
        var extra = settings.RequiresOAuth ? " " + _strings.Format("AccountEditor_RequiresOAuth", settings.ProviderName ?? string.Empty)
            : settings.RequiresAppPassword ? " " + _strings.Format("AccountEditor_RequiresAppPassword", settings.ProviderName ?? string.Empty)
            : string.Empty;
        Hint = source + extra;
    }

    private Account? BuildAccount(string address)
    {
        if (!MailAddressRules.TryNormalizeHost(ImapHost, out var imapHost)
            || !MailAddressRules.TryNormalizeHost(SmtpHost, out var smtpHost)
            || !IsPort(ImapPort) || !IsPort(SmtpPort))
        {
            return null;
        }

        return new Account
        {
            Address = address,
            DisplayName = DisplayName.Trim(),
            UserName = string.IsNullOrWhiteSpace(UserName) ? address : UserName.Trim(),
            Imap = new MailServer { Host = imapHost, Port = (int)ImapPort, Security = ImapSecurityIndex == 1 ? MailSecurity.StartTls : MailSecurity.SslOnConnect },
            Smtp = new MailServer { Host = smtpHost, Port = (int)SmtpPort, Security = SmtpSecurityIndex == 1 ? MailSecurity.StartTls : MailSecurity.SslOnConnect },
        };
    }

    private static bool IsPort(double value) => !double.IsNaN(value) && value is >= 1 and <= 65535 && value == Math.Floor(value);
}
