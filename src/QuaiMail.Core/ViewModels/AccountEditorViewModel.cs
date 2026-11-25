using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Accounts.OAuth;
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
    private readonly AccessTokens? _tokens;
    private readonly OAuthFlow? _flow;
    private string? _detectedFor;

    public AccountEditorViewModel(
        MailSettingsDiscovery discovery,
        IMailService mail,
        AccountStore accounts,
        ICredentialStore credentials,
        IStrings strings,
        ILogger<AccountEditorViewModel>? logger = null,
        AccessTokens? tokens = null,
        OAuthFlow? flow = null)
    {
        _tokens = tokens;
        _flow = flow;
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
    [NotifyPropertyChangedFor(nameof(CanClickSignInWithGoogle))]
    [NotifyPropertyChangedFor(nameof(CanClickSignInWithMicrosoft))]
    public partial bool IsBusy { get; set; }

    /// <summary>La page de connexion du fournisseur est ouverte dans le navigateur : Quai Mail attend son retour.</summary>
    [ObservableProperty]
    public partial bool IsWaitingForBrowser { get; set; }

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

    /// <summary>Connexion par le navigateur possible avec Google (Quai Mail inscrit chez Google).</summary>
    public bool CanSignInWithGoogle => _flow is not null && _tokens?.Clients.For(AccountAuth.Google) is not null;

    /// <summary>Connexion par le navigateur possible avec Microsoft (Outlook.com, Microsoft 365).</summary>
    public bool CanSignInWithMicrosoft => _flow is not null && _tokens?.Clients.For(AccountAuth.Microsoft) is not null;

    public bool CanSignIn => CanSignInWithGoogle || CanSignInWithMicrosoft;

    public bool CanClickSignInWithGoogle => CanSignInWithGoogle && !IsBusy;

    public bool CanClickSignInWithMicrosoft => CanSignInWithMicrosoft && !IsBusy;

    /// <summary>Texte du bouton : « (à venir) » tant que la connexion par le navigateur n'est pas proposée.</summary>
    public string GoogleSignInText => _strings.Get(CanSignInWithGoogle ? "AccountDialog_SignInGoogle" : "AccountDialog_SignInGoogleSoon");

    public string MicrosoftSignInText => _strings.Get(CanSignInWithMicrosoft ? "AccountDialog_SignInMicrosoft" : "AccountDialog_SignInMicrosoftSoon");

    public string SignInHint => _strings.Get(CanSignIn ? "AccountDialog_SignInHint" : "AccountDialog_SignInSoon");

    /// <summary>
    /// Connexion par le navigateur (Gmail, Outlook.com) : la page officielle du fournisseur s'ouvre ; Quai Mail ne voit
    /// jamais le mot de passe. Le compte est vérifié (IMAP et SMTP, sans envoi) avant d'être enregistré.
    /// </summary>
    public async Task<Account?> SignInAsync(AccountAuth auth, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var address = Address.Trim();
        if (!MailAddressRules.IsValidAddress(address))
        {
            ErrorMessage = _strings.Get("AccountEditor_InvalidAddress");
            return null;
        }

        if (_flow is null || _tokens is null || _tokens.Clients.For(auth) is not { } provider)
        {
            ErrorMessage = _strings.Get("AccountEditor_SignInUnavailable");
            return null;
        }

        var known = KnownProviders.Find(address);
        var imap = known?.Imap ?? (auth == AccountAuth.Google
            ? new MailServer { Host = "imap.gmail.com", Port = 993 }
            : new MailServer { Host = "outlook.office365.com", Port = 993 });
        var smtp = known?.Smtp ?? (auth == AccountAuth.Google
            ? new MailServer { Host = "smtp.gmail.com", Port = 465 }
            : new MailServer { Host = "smtp.office365.com", Port = 587, Security = MailSecurity.StartTls });
        var account = !string.IsNullOrWhiteSpace(ImapHost) && BuildAccount(address) is { } form
            ? form with { UserName = address, Auth = auth }
            : new Account { Address = address, DisplayName = DisplayName.Trim(), UserName = address, Imap = imap, Smtp = smtp, Auth = auth };
        if (_accounts.All.Any(a => string.Equals(a.Address, address, StringComparison.OrdinalIgnoreCase) && a.Imap.Host == account.Imap.Host))
        {
            ErrorMessage = _strings.Get("AccountEditor_AlreadyAdded");
            return null;
        }

        IsBusy = true;
        try
        {
            var tokens = await WaitForBrowserAsync(_flow.SignInAsync(provider, address, cancellationToken));
            await _mail.VerifyAsync(account, tokens.AccessToken, cancellationToken);
            _tokens.Store(account, tokens);
            _accounts.Save(account);
            SavedAccount = account;
            _logger.LogInformation("Compte {Id} ajouté (connexion {Provider})", account.Id, provider.Name);
            return account;
        }
        catch (OAuthException ex)
        {
            ErrorMessage = _strings.Get("OAuthError_" + ex.Error);
            return null;
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Autorisation expirée ou retirée (Google la redemande tous les 7 jours tant que Quai Mail est « en test ») : nouvelle
    /// connexion par le navigateur pour ce compte ; ses réglages, son cache et ses mails ne changent pas.
    /// </summary>
    public async Task<bool> ReconnectAsync(Account account, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        if (_flow is null || _tokens is null || _tokens.Clients.For(account.Auth) is not { } provider)
        {
            ErrorMessage = _strings.Get("AccountEditor_SignInUnavailable");
            return false;
        }

        IsBusy = true;
        try
        {
            var tokens = await WaitForBrowserAsync(_flow.SignInAsync(provider, account.UserName, cancellationToken));
            await _mail.VerifyAsync(account, tokens.AccessToken, cancellationToken);
            _tokens.Store(account, tokens);
            _logger.LogInformation("Compte {Id} reconnecté ({Provider})", account.Id, provider.Name);
            return true;
        }
        catch (OAuthException ex)
        {
            ErrorMessage = _strings.Get("OAuthError_" + ex.Error);
            return false;
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<OAuthTokens> WaitForBrowserAsync(Task<OAuthTokens> signIn)
    {
        IsWaitingForBrowser = true;
        try
        {
            return await signIn;
        }
        finally
        {
            IsWaitingForBrowser = false;
        }
    }

    /// <summary>Connexion par le navigateur adaptée à ces réglages (serveurs de Google ou de Microsoft), si elle est proposée.</summary>
    public AccountAuth? SignInFor(MailSettings settings)
    {
        var auth = settings.Imap.Host.ToLowerInvariant() switch
        {
            "imap.gmail.com" => AccountAuth.Google,
            "outlook.office365.com" or "imap-mail.outlook.com" => AccountAuth.Microsoft,
            _ => (AccountAuth?)null,
        };
        return auth is { } value && _flow is not null && _tokens?.Clients.For(value) is not null ? value : null;
    }

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
        var provider = settings.ProviderName ?? string.Empty;
        var signIn = SignInFor(settings) switch
        {
            AccountAuth.Google => _strings.Get("AccountDialog_SignInGoogle"),
            AccountAuth.Microsoft => _strings.Get("AccountDialog_SignInMicrosoft"),
            _ => null,
        };
        var extra = (settings.RequiresOAuth, settings.RequiresAppPassword, signIn) switch
        {
            (true, _, { } button) => " " + _strings.Format("AccountEditor_UseSignIn", provider, button),
            (true, _, null) => " " + _strings.Format("AccountEditor_RequiresOAuth", provider),
            (_, true, { } button) => " " + _strings.Format("AccountEditor_AppPasswordOrSignIn", provider, button),
            (_, true, null) => " " + _strings.Format("AccountEditor_RequiresAppPassword", provider),
            (_, _, { } button) => " " + _strings.Format("AccountEditor_SignInOffered", button),
            _ => string.Empty,
        };
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
