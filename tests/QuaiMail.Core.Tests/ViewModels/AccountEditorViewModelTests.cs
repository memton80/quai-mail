using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.ViewModels;

/// <summary>Ajout d'un compte de bout en bout : formulaire, vraie vérification MailKit contre les faux serveurs, coffre, fichier.</summary>
public sealed class AccountEditorViewModelTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly FakeImapServer _imap = new();
    private readonly FakeSmtpServer _smtp = new(MailSecurity.StartTls);
    private readonly MemoryCredentialStore _credentials = new();
    private AccountStore _accounts = null!;
    private MailKitMailService _mail = null!;
    private ProviderAutoconfig _autoconfig = null!;

    public ValueTask InitializeAsync()
    {
        _imap.Folders.Add(new FakeFolder("INBOX"));
        _accounts = new AccountStore(new AppPaths(_temp.Path));
        _mail = new MailKitMailService(_credentials, null, TestCertificate.Pinned) { Timeout = TimeSpan.FromSeconds(10) };
        _autoconfig = new ProviderAutoconfig(new NoNetwork());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _autoconfig.Dispose();
        await _mail.DisposeAsync();
        await _imap.DisposeAsync();
        await _smtp.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_verified_account_is_saved_with_its_password_in_the_vault_only()
    {
        var editor = Editor();
        editor.Address = "alice@example.org";
        editor.DisplayName = "Alice";
        editor.Password = _imap.Password;
        await editor.DetectAsync(TestContext.Current.CancellationToken);
        Assert.Equal("imap.example.org", editor.ImapHost);
        Assert.Equal("AccountEditor_SourceGuess", editor.Hint);
        FillServers(editor);

        var account = await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(account);
        Assert.Null(editor.ErrorMessage);
        Assert.Equal("Alice", account.DisplayName);
        Assert.Equal(MailSecurity.StartTls, account.Smtp.Security);
        Assert.Equal(_imap.Password, _credentials.GetPassword(account.Id));
        Assert.Equal(account, Assert.Single(_accounts.All));
        Assert.DoesNotContain(_imap.Password, File.ReadAllText(new AppPaths(_temp.Path).AccountsFile), StringComparison.Ordinal);
        Assert.Equal(string.Empty, editor.Password);
        Assert.Equal(0, _smtp.SendAttempts);
    }

    [Fact]
    public async Task A_refused_password_saves_nothing_and_says_why()
    {
        var editor = Editor();
        editor.Address = "alice@example.org";
        editor.Password = "faux";
        FillServers(editor);

        Assert.Null(await editor.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal($"MailError_Format(IMAP,localhost:{_imap.Port},MailError_Authentication)", editor.ErrorMessage);
        Assert.Empty(_accounts.All);
        Assert.Empty(_credentials.Passwords);
        Assert.True(editor.ShowServerSettings);
    }

    [Theory]
    [InlineData("pas une adresse", "secret", "AccountEditor_InvalidAddress")]
    [InlineData("alice@example.org", "", "AccountEditor_MissingPassword")]
    public async Task Incomplete_forms_are_refused_before_any_connection(string address, string password, string error)
    {
        var editor = Editor();
        editor.Address = address;
        editor.Password = password;

        Assert.Null(await editor.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(error, editor.ErrorMessage);
        Assert.Equal(0, _imap.ConnectionCount);
    }

    [Fact]
    public async Task An_invalid_server_name_is_refused_before_any_connection()
    {
        var editor = Editor();
        editor.Address = "alice@example.org";
        editor.Password = "x";
        FillServers(editor);
        editor.ImapHost = "pas un nom/de serveur";

        Assert.Null(await editor.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal("AccountEditor_InvalidServer", editor.ErrorMessage);
        Assert.Equal(0, _imap.ConnectionCount);
    }

    [Fact]
    public async Task A_known_provider_is_filled_in_with_its_advice()
    {
        var editor = Editor();
        editor.Address = "quelqu.un@gmail.com";

        await editor.DetectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("imap.gmail.com", editor.ImapHost);
        Assert.Equal(465, editor.SmtpPort);
        Assert.Equal("quelqu.un@gmail.com", editor.UserName);
        Assert.Equal("AccountEditor_SourceKnown(Gmail) AccountEditor_RequiresAppPassword(Gmail)", editor.Hint);
    }

    [Fact]
    public async Task The_same_account_cannot_be_added_twice()
    {
        var first = Editor();
        first.Address = "alice@example.org";
        first.Password = _imap.Password;
        FillServers(first);
        Assert.NotNull(await first.SaveAsync(TestContext.Current.CancellationToken));

        var second = Editor();
        second.Address = "Alice@Example.org";
        second.Password = _imap.Password;
        FillServers(second);

        Assert.Null(await second.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal("AccountEditor_AlreadyAdded", second.ErrorMessage);
    }

    private AccountEditorViewModel Editor() =>
        new(new MailSettingsDiscovery(_autoconfig), _mail, _accounts, _credentials, new KeyStrings());

    private void FillServers(AccountEditorViewModel editor)
    {
        editor.ImapHost = "localhost";
        editor.ImapPort = _imap.Port;
        editor.ImapSecurityIndex = 0;
        editor.SmtpHost = "localhost";
        editor.SmtpPort = _smtp.Port;
        editor.SmtpSecurityIndex = 1;
        editor.UserName = _imap.UserName;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Pas de réseau dans les tests");
    }
}
