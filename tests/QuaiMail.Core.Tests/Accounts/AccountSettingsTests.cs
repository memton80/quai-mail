using System.Net;
using System.Text;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Accounts;

public sealed class AccountSettingsTests
{
    [Theory]
    [InlineData("alice@example.org", "example.org")]
    [InlineData("  Alice.Martin@Exemple.FR ", "exemple.fr")]
    [InlineData("bob@élysée.fr", "xn--lyse-9oad.fr")]
    public void A_valid_address_gives_its_domain(string address, string domain)
    {
        Assert.True(MailAddressRules.TryGetDomain(address, out var found));
        Assert.Equal(domain, found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("alice")]
    [InlineData("alice@")]
    [InlineData("@example.org")]
    [InlineData("alice@localhost")]
    [InlineData("alice @example.org")]
    [InlineData("Alice <alice@example.org>")]
    [InlineData("alice@exa_mple.org")]
    [InlineData("alice@-example.org")]
    public void An_invalid_address_is_refused(string address) => Assert.False(MailAddressRules.IsValidAddress(address));

    [Fact]
    public void Known_providers_need_no_connection()
    {
        var gmail = KnownProviders.Find("someone@gmail.com");
        Assert.NotNull(gmail);
        Assert.Equal("imap.gmail.com", gmail.Imap.Host);
        Assert.Equal(993, gmail.Imap.Port);
        Assert.Equal(MailSecurity.SslOnConnect, gmail.Imap.Security);
        Assert.Equal("someone@gmail.com", gmail.UserName);
        Assert.True(gmail.RequiresAppPassword);
        Assert.Equal(MailSettingsSource.KnownProvider, gmail.Source);

        var outlook = KnownProviders.Find("someone@hotmail.fr");
        Assert.True(outlook!.RequiresOAuth);
        Assert.Equal(MailSecurity.StartTls, outlook.Smtp.Security);

        Assert.Equal("imap.orange.fr", KnownProviders.Find("x@wanadoo.fr")!.Imap.Host);
        Assert.Null(KnownProviders.Find("x@example.org"));
    }

    [Fact]
    public void The_guess_uses_the_usual_names_and_encrypted_ports()
    {
        var guess = KnownProviders.Guess("x@example.org")!;

        Assert.Equal(new MailServer { Host = "imap.example.org", Port = 993, Security = MailSecurity.SslOnConnect }, guess.Imap);
        Assert.Equal(new MailServer { Host = "smtp.example.org", Port = 465, Security = MailSecurity.SslOnConnect }, guess.Smtp);
        Assert.Equal(MailSettingsSource.Guess, guess.Source);
    }

    private const string Autoconfig = """
        <?xml version="1.0" encoding="UTF-8"?>
        <clientConfig version="1.1">
          <emailProvider id="example.org">
            <domain>example.org</domain>
            <displayName>Exemple Mail</displayName>
            <incomingServer type="pop3">
              <hostname>pop.example.org</hostname><port>995</port><socketType>SSL</socketType><username>%EMAILADDRESS%</username>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap-clair.example.org</hostname><port>143</port><socketType>plain</socketType><username>%EMAILADDRESS%</username>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap.%EMAILDOMAIN%</hostname><port>143</port><socketType>STARTTLS</socketType><username>%EMAILLOCALPART%</username>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>mail.example.org</hostname><port>993</port><socketType>SSL</socketType><username>%EMAILLOCALPART%</username>
              <authentication>password-cleartext</authentication>
            </incomingServer>
            <outgoingServer type="smtp">
              <hostname>smtp.example.org</hostname><port>587</port><socketType>STARTTLS</socketType><username>%EMAILADDRESS%</username>
            </outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    [Fact]
    public void The_provider_file_gives_encrypted_servers_only_ssl_first()
    {
        var settings = ProviderAutoconfig.Parse(Autoconfig, "alice@example.org");

        Assert.NotNull(settings);
        Assert.Equal(new MailServer { Host = "mail.example.org", Port = 993, Security = MailSecurity.SslOnConnect }, settings.Imap);
        Assert.Equal(new MailServer { Host = "smtp.example.org", Port = 587, Security = MailSecurity.StartTls }, settings.Smtp);
        Assert.Equal("alice", settings.UserName);
        Assert.Equal("Exemple Mail", settings.ProviderName);
        Assert.Equal(MailSettingsSource.ProviderAutoconfig, settings.Source);
        Assert.False(settings.RequiresOAuth);
    }

    [Theory]
    [InlineData("""<clientConfig><emailProvider><incomingServer type="imap"><hostname>imap.example.org</hostname><port>143</port><socketType>plain</socketType></incomingServer><outgoingServer type="smtp"><hostname>smtp.example.org</hostname><port>25</port><socketType>plain</socketType></outgoingServer></emailProvider></clientConfig>""")]
    [InlineData("pas du XML")]
    [InlineData("""<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><clientConfig>&e;</clientConfig>""")]
    [InlineData("<clientConfig/>")]
    public void Unencrypted_or_unusable_files_give_nothing(string xml) => Assert.Null(ProviderAutoconfig.Parse(xml, "alice@example.org"));

    [Fact]
    public async Task Only_the_domain_of_the_provider_is_asked_over_https()
    {
        var handler = new Answers(request =>
            request.RequestUri!.Host == "example.org" ? (HttpStatusCode.OK, Autoconfig) : (HttpStatusCode.NotFound, string.Empty));
        using var autoconfig = new ProviderAutoconfig(handler);

        var settings = await autoconfig.TryGetAsync("alice@example.org", TestContext.Current.CancellationToken);

        Assert.Equal("mail.example.org", settings?.Imap.Host);
        Assert.Equal(
            ["https://autoconfig.example.org/mail/config-v1.1.xml", "https://example.org/.well-known/autoconfig/mail/config-v1.1.xml"],
            handler.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        Assert.All(handler.Requests, r => Assert.DoesNotContain("alice", r.RequestUri!.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_published_or_unreachable_gives_nothing()
    {
        using var autoconfig = new ProviderAutoconfig(new Answers(_ => throw new HttpRequestException("hors ligne")));

        Assert.Null(await autoconfig.TryGetAsync("alice@example.org", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_oversized_answer_is_ignored()
    {
        var huge = Autoconfig + new string(' ', ProviderAutoconfig.MaxResponseBytes);
        using var autoconfig = new ProviderAutoconfig(new Answers(_ => (HttpStatusCode.OK, huge)));

        Assert.Null(await autoconfig.TryGetAsync("alice@example.org", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Discovery_prefers_the_built_in_table_then_the_provider_then_a_guess()
    {
        var handler = new Answers(request => request.RequestUri!.Host.EndsWith("example.org", StringComparison.Ordinal) ? (HttpStatusCode.OK, Autoconfig) : (HttpStatusCode.NotFound, string.Empty));
        using var autoconfig = new ProviderAutoconfig(handler);
        var discovery = new MailSettingsDiscovery(autoconfig);

        Assert.Equal(MailSettingsSource.KnownProvider, (await discovery.DiscoverAsync("x@free.fr", TestContext.Current.CancellationToken))!.Source);
        Assert.Empty(handler.Requests);
        Assert.Equal(MailSettingsSource.ProviderAutoconfig, (await discovery.DiscoverAsync("x@example.org", TestContext.Current.CancellationToken))!.Source);
        Assert.Equal(MailSettingsSource.Guess, (await discovery.DiscoverAsync("x@example.net", TestContext.Current.CancellationToken))!.Source);
        Assert.Null(await discovery.DiscoverAsync("pas une adresse", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Accounts_are_saved_without_any_password()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new AccountStore(paths);
        var account = new Account
        {
            Address = "alice@example.org",
            DisplayName = "Alice",
            UserName = "alice",
            Imap = new MailServer { Host = "imap.example.org", Port = 993 },
            Smtp = new MailServer { Host = "smtp.example.org", Port = 587, Security = MailSecurity.StartTls },
        };
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Save(account);

        var json = File.ReadAllText(paths.AccountsFile);
        Assert.Contains("\"security\": \"startTls\"", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(account, Assert.Single(new AccountStore(paths).All));
        Assert.Equal(1, changes);

        store.Save(account with { DisplayName = "Alice M." });
        Assert.Equal("Alice M.", new AccountStore(paths).Find(account.Id)!.DisplayName);

        Assert.True(store.Remove(account.Id));
        Assert.Empty(new AccountStore(paths).All);
        Assert.False(store.Remove(account.Id));
    }

    [Fact]
    public void An_unreadable_account_list_is_set_aside()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        File.WriteAllText(paths.AccountsFile, "{ pas du json");

        Assert.Empty(new AccountStore(paths).All);
        Assert.Single(Directory.GetFiles(temp.Path, "accounts.json.corrompu-*"));
    }

    private sealed class Answers(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml"), RequestMessage = request });
        }
    }
}
