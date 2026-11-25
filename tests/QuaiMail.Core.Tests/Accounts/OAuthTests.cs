using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Accounts.OAuth;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.Accounts;

/// <summary>
/// Connexion par le navigateur (OAuth2 + PKCE) contre un faux fournisseur : le « navigateur » est simulé (il suit le
/// retour vers 127.0.0.1), le point d'émission des jetons est un faux serveur en mémoire. Jamais de vrai compte.
/// </summary>
public sealed class OAuthTests : IAsyncLifetime
{
    private static readonly OAuthProvider Google = OAuthProvider.Google("client-google", "secret-google") with { TokenEndpoint = new Uri("https://jetons.example/token") };

    private readonly TokenEndpoint _endpoint = new();
    private readonly Browser _browser = new();
    private readonly MemoryCredentialStore _credentials = new();
    private OAuthFlow _flow = null!;

    public ValueTask InitializeAsync()
    {
        _flow = new OAuthFlow(_browser, _endpoint) { SignInTimeout = TimeSpan.FromSeconds(10) };
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _flow.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Signing_in_uses_the_official_page_pkce_and_a_local_return()
    {
        var tokens = await _flow.SignInAsync(Google, "alice@gmail.com", TestContext.Current.CancellationToken);

        Assert.Equal("acces-1", tokens.AccessToken);
        Assert.Equal("renouvellement-1", tokens.RefreshToken);
        var opened = Assert.Single(_browser.Opened);
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", opened.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(opened.Query);
        Assert.Equal("client-google", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://mail.google.com/", query["scope"]);
        Assert.Equal("alice@gmail.com", query["login_hint"]);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"], StringComparison.Ordinal);

        // Le vérificateur PKCE envoyé avec le code correspond au défi de la page de connexion.
        var form = Assert.Single(_endpoint.Requests);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-recu", form["code"]);
        Assert.Equal("secret-google", form["client_secret"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        Assert.Equal(query["redirect_uri"], form["redirect_uri"]);
        // Le navigateur lit la page de retour un peu après que Quai Mail a reçu le code.
        for (var i = 0; i < 100 && _browser.Pages.IsEmpty; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.StartsWith("Quai Mail : connexion réussie", Assert.Single(_browser.Pages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_return_that_does_not_match_the_request_is_refused()
    {
        _browser.State = "falsifie";

        var error = await Assert.ThrowsAsync<OAuthException>(() => _flow.SignInAsync(Google, null, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthError.Invalid, error.Error);
        Assert.Empty(_endpoint.Requests);
    }

    [Fact]
    public async Task A_refusal_on_the_provider_page_is_reported()
    {
        _browser.Error = "access_denied";

        var error = await Assert.ThrowsAsync<OAuthException>(() => _flow.SignInAsync(Google, null, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthError.Denied, error.Error);
    }

    [Fact]
    public async Task A_closed_page_ends_with_a_timeout()
    {
        _browser.Follow = false;
        using var flow = new OAuthFlow(_browser, _endpoint) { SignInTimeout = TimeSpan.FromMilliseconds(300) };

        var error = await Assert.ThrowsAsync<OAuthException>(() => flow.SignInAsync(Google, null, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthError.Timeout, error.Error);
    }

    [Fact]
    public async Task Access_tokens_are_renewed_from_the_vault_and_rotated()
    {
        var account = Account(AccountAuth.Google);
        _credentials.SetPassword(account.Id, "renouvellement-0");
        var tokens = new AccessTokens(_credentials, _flow, new OAuthClients("client-google", "secret-google", null));
        _endpoint.Provider = Google;

        // Le fournisseur configuré est le vrai Google ; ses adresses sont remplacées dans ce test par le faux point d'émission.
        var first = await tokens.GetAsync(account with { }, TestContext.Current.CancellationToken);
        var second = await tokens.GetAsync(account, TestContext.Current.CancellationToken);

        Assert.Equal("acces-1", first);
        Assert.Equal(first, second);
        Assert.Single(_endpoint.Requests);
        Assert.Equal(("refresh_token", "renouvellement-0"), (_endpoint.Requests[0]["grant_type"], _endpoint.Requests[0]["refresh_token"]));
        Assert.Equal("renouvellement-1", _credentials.GetPassword(account.Id));
    }

    [Fact]
    public async Task A_withdrawn_authorization_asks_to_sign_in_again()
    {
        var account = Account(AccountAuth.Google);
        _credentials.SetPassword(account.Id, "retire");
        _endpoint.Refuse = "invalid_grant";
        var tokens = new AccessTokens(_credentials, _flow, new OAuthClients("client-google", "secret-google", null));

        var error = await Assert.ThrowsAsync<OAuthException>(() => tokens.GetAsync(account, TestContext.Current.CancellationToken));

        Assert.Equal(OAuthError.Expired, error.Error);
    }

    [Fact]
    public async Task A_signed_in_account_reads_and_sends_with_xoauth2_without_any_password()
    {
        await using var imap = new FakeImapServer();
        await using var smtp = new FakeSmtpServer();
        imap.Folders.Add(new FakeFolder("INBOX"));
        imap.UserName = smtp.UserName = "alice@gmail.com";
        imap.AccessToken = smtp.AccessToken = "acces-1";
        using var temp = new TempDirectory();
        var accounts = new AccountStore(new AppPaths(temp.Path));
        var tokens = new AccessTokens(_credentials, _flow, new OAuthClients("client-google", "secret-google", null));
        await using var mail = new MailKitMailService(_credentials, null, TestCertificate.Pinned, tokens) { Timeout = TimeSpan.FromSeconds(10) };
        using var autoconfig = new ProviderAutoconfig(new NoNetwork());
        var editor = new AccountEditorViewModel(new MailSettingsDiscovery(autoconfig), mail, accounts, _credentials, new KeyStrings(), null, tokens, _flow)
        {
            Address = "alice@gmail.com",
            ImapHost = "localhost",
            ImapPort = imap.Port,
            SmtpHost = "localhost",
            SmtpPort = smtp.Port,
        };
        Assert.True(editor.CanSignInWithGoogle);
        Assert.False(editor.CanSignInWithMicrosoft);

        var account = await editor.SignInAsync(AccountAuth.Google, TestContext.Current.CancellationToken);

        Assert.NotNull(account);
        Assert.Equal(AccountAuth.Google, account.Auth);
        Assert.Equal("renouvellement-1", _credentials.GetPassword(account.Id));
        Assert.Contains("AUTHENTICATE", imap.Commands);
        Assert.Equal(0, smtp.SendAttempts);
        Assert.Contains("\"auth\": \"google\"", File.ReadAllText(new AppPaths(temp.Path).AccountsFile), StringComparison.Ordinal);

        // Plus tard : le jeton d'accès en mémoire sert directement.
        var folders = await mail.GetFoldersAsync(account, TestContext.Current.CancellationToken);
        Assert.Contains(folders, f => f.FullName == "INBOX");
    }

    [Fact]
    public async Task An_expired_authorization_is_renewed_by_signing_in_again()
    {
        await using var imap = new FakeImapServer();
        await using var smtp = new FakeSmtpServer();
        imap.Folders.Add(new FakeFolder("INBOX"));
        imap.UserName = smtp.UserName = "alice@gmail.com";
        imap.AccessToken = smtp.AccessToken = "acces-1";
        var account = Account(AccountAuth.Google) with
        {
            Imap = new MailServer { Host = "localhost", Port = imap.Port },
            Smtp = new MailServer { Host = "localhost", Port = smtp.Port },
        };
        _credentials.SetPassword(account.Id, "retire");
        var tokens = new AccessTokens(_credentials, _flow, new OAuthClients("client-google", "secret-google", null));
        await using var mail = new MailKitMailService(_credentials, null, TestCertificate.Pinned, tokens) { Timeout = TimeSpan.FromSeconds(10) };

        // Le jeton d'accès en mémoire est refusé par le serveur (autorisation retirée) : erreur claire, jeton oublié.
        tokens.Store(account, new OAuthTokens("perime", null, DateTimeOffset.UtcNow.AddHours(1)));
        var refused = await Assert.ThrowsAsync<MailConnectionException>(() => mail.GetFoldersAsync(account, TestContext.Current.CancellationToken));
        Assert.Equal(MailError.SignInExpired, refused.Error);

        // Le renouvellement est refusé à son tour : toujours « se reconnecter ».
        _endpoint.Refuse = "invalid_grant";
        var expired = await Assert.ThrowsAsync<MailConnectionException>(() => mail.GetFoldersAsync(account, TestContext.Current.CancellationToken));
        Assert.Equal(MailError.SignInExpired, expired.Error);

        // « Se reconnecter » : nouvelle page de connexion, nouveau jeton de renouvellement, même compte.
        _endpoint.Refuse = null;
        using var temp = new TempDirectory();
        using var autoconfig = new ProviderAutoconfig(new NoNetwork());
        var editor = new AccountEditorViewModel(new MailSettingsDiscovery(autoconfig), mail, new AccountStore(new AppPaths(temp.Path)), _credentials, new KeyStrings(), null, tokens, _flow);

        Assert.True(await editor.ReconnectAsync(account, TestContext.Current.CancellationToken));

        Assert.Equal("alice@gmail.com", HttpUtility.ParseQueryString(_browser.Opened.Last().Query)["login_hint"]);
        Assert.Equal("renouvellement-1", _credentials.GetPassword(account.Id));
        var folders = await mail.GetFoldersAsync(account, TestContext.Current.CancellationToken);
        Assert.Contains(folders, f => f.FullName == "INBOX");
    }

    [Fact]
    public async Task The_form_points_gmail_and_outlook_users_to_the_browser_sign_in()
    {
        using var autoconfig = new ProviderAutoconfig(new NoNetwork());
        using var temp = new TempDirectory();
        AccountEditorViewModel Editor(OAuthClients clients) => new(
            new MailSettingsDiscovery(autoconfig), new FakeMailService(), new AccountStore(new AppPaths(temp.Path)), _credentials, new KeyStrings(), null,
            new AccessTokens(_credentials, _flow, clients), _flow);

        var both = new OAuthClients("client-google", "secret-google", "client-ms");
        var outlook = Editor(both);
        outlook.Address = "bob@outlook.fr";
        await outlook.DetectAsync(TestContext.Current.CancellationToken);
        Assert.Contains("AccountEditor_UseSignIn(Outlook.com,AccountDialog_SignInMicrosoft)", outlook.Hint, StringComparison.Ordinal);

        var gmail = Editor(both);
        gmail.Address = "alice@gmail.com";
        await gmail.DetectAsync(TestContext.Current.CancellationToken);
        Assert.Contains("AccountEditor_AppPasswordOrSignIn(Gmail,AccountDialog_SignInGoogle)", gmail.Hint, StringComparison.Ordinal);

        // Copie compilée sans inscription : pas de bouton, le texte le dit.
        var none = Editor(OAuthClients.None);
        none.Address = "bob@outlook.fr";
        await none.DetectAsync(TestContext.Current.CancellationToken);
        Assert.Contains("AccountEditor_RequiresOAuth(Outlook.com)", none.Hint, StringComparison.Ordinal);
        Assert.False(none.CanSignIn);

        // Pas encore proposée : boutons grisés, marqués « à venir ».
        Assert.False(none.CanClickSignInWithGoogle || none.CanClickSignInWithMicrosoft);
        Assert.Equal(("AccountDialog_SignInGoogleSoon", "AccountDialog_SignInMicrosoftSoon", "AccountDialog_SignInSoon"), (none.GoogleSignInText, none.MicrosoftSignInText, none.SignInHint));
        Assert.Equal(("AccountDialog_SignInGoogle", "AccountDialog_SignInMicrosoft"), (outlook.GoogleSignInText, outlook.MicrosoftSignInText));
        Assert.True(outlook.CanClickSignInWithMicrosoft);
    }

    [Fact]
    public void Without_registration_no_browser_sign_in_is_offered()
    {
        Assert.Null(OAuthClients.None.For(AccountAuth.Google));
        Assert.Null(new OAuthClients(" ", null, "").For(AccountAuth.Microsoft));
        Assert.Equal("localhost", new OAuthClients(null, null, "client-ms").For(AccountAuth.Microsoft)!.RedirectHost);
    }

    private static Account Account(AccountAuth auth) => new()
    {
        Address = "alice@gmail.com",
        UserName = "alice@gmail.com",
        Imap = new MailServer { Host = "imap.gmail.com", Port = 993 },
        Smtp = new MailServer { Host = "smtp.gmail.com", Port = 465 },
        Auth = auth,
    };

    /// <summary>Navigateur simulé : suit le retour vers l'ordinateur, comme après une connexion réussie.</summary>
    private sealed class Browser : QuaiMail.Core.Services.ILauncher
    {
        public List<Uri> Opened { get; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<string> Pages { get; } = new();

        public bool Follow { get; set; } = true;

        public string? State { get; set; }

        public string? Error { get; set; }

        public bool Open(Uri uri)
        {
            Opened.Add(uri);
            if (!Follow)
            {
                return true;
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            var back = Error is null
                ? $"{query["redirect_uri"]}?code=code-recu&state={Uri.EscapeDataString(State ?? query["state"]!)}"
                : $"{query["redirect_uri"]}?error={Error}&state={Uri.EscapeDataString(query["state"]!)}";
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                Pages.Enqueue(await http.GetStringAsync(back));
            });
            return true;
        }
    }

    /// <summary>Faux point d'émission des jetons (aucun réseau).</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private int _issued;

        public List<Dictionary<string, string>> Requests { get; } = [];

        public string? Refuse { get; set; }

        public OAuthProvider? Provider { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var parsed = HttpUtility.ParseQueryString(body);
            Requests.Add(parsed.AllKeys.ToDictionary(k => k!, k => parsed[k]!));
            if (Refuse is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($$"""{ "error": "{{Refuse}}" }""") };
            }

            _issued++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "access_token": "acces-{{_issued}}", "refresh_token": "renouvellement-{{_issued}}", "expires_in": 3599, "token_type": "Bearer" }"""),
            };
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Pas de réseau");
    }
}
