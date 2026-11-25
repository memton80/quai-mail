using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Mail;

/// <summary>Le vrai service (MailKit) contre des faux serveurs IMAP / SMTP locaux en TLS : jamais de vrai compte.</summary>
public sealed class MailKitMailServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Newest = new(2026, 11, 25, 9, 30, 0, TimeSpan.FromHours(1));

    private readonly FakeImapServer _imap = new();
    private readonly FakeSmtpServer _smtp = new();
    private readonly MemoryCredentialStore _credentials = new();
    private MailKitMailService _service = null!;

    public ValueTask InitializeAsync()
    {
        _imap.Folders.Add(new FakeFolder("INBOX"));
        _imap.Folders.Add(new FakeFolder("Brouillons", @"\Drafts"));
        _imap.Folders.Add(new FakeFolder("Envoyés", @"\Sent"));
        _imap.Folders.Add(new FakeFolder("Corbeille", @"\Trash"));
        _imap.Folders.Add(new FakeFolder("Archives", @"\Noselect"));
        _imap.Folders.Add(new FakeFolder("Archives/2026"));
        FakeImapServer.Fill(_imap.Folder("INBOX"), 120, Newest);
        _imap.Folder("Archives/2026").Messages.Add(new FakeMessage(7, "Facture d'électricité", "EDF", "facture@example.org", Newest.AddDays(-30), Seen: true, Flagged: true));
        _service = new MailKitMailService(_credentials, null, TestCertificate.Pinned) { Timeout = TimeSpan.FromSeconds(10) };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _imap.DisposeAsync();
        await _smtp.DisposeAsync();
    }

    [Fact]
    public async Task Verify_logs_in_to_imap_and_smtp_without_sending_anything()
    {
        await _service.VerifyAsync(Account(), _imap.Password, TestContext.Current.CancellationToken);

        Assert.Contains("AUTHENTICATE", _imap.Commands);
        Assert.Contains("AUTH", _smtp.Commands);
        Assert.Equal(0, _smtp.SendAttempts);
        Assert.DoesNotContain(_smtp.Commands, c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase) || c.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_works_with_starttls_too()
    {
        await using var imap = new FakeImapServer(MailSecurity.StartTls);
        await using var smtp = new FakeSmtpServer(MailSecurity.StartTls);
        imap.Folders.Add(new FakeFolder("INBOX"));
        var account = Account() with { Imap = imap.Endpoint, Smtp = smtp.Endpoint };

        await _service.VerifyAsync(account, imap.Password, TestContext.Current.CancellationToken);

        Assert.Contains("STARTTLS", imap.Commands);
        Assert.Contains("STARTTLS", smtp.Commands);
    }

    [Fact]
    public async Task A_wrong_password_is_an_authentication_error_that_never_contains_the_password()
    {
        var error = await Assert.ThrowsAsync<MailConnectionException>(
            () => _service.VerifyAsync(Account(), "mauvais secret", TestContext.Current.CancellationToken));

        Assert.Equal(MailError.Authentication, error.Error);
        Assert.Equal("IMAP", error.Protocol);
        Assert.Equal($"localhost:{_imap.Port}", error.Server);
        Assert.DoesNotContain("mauvais secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_certificate_that_windows_does_not_trust_is_refused()
    {
        // Validation normale (celle de l'application) : le certificat auto-signé du faux serveur est refusé.
        await using var strict = new MailKitMailService(_credentials) { Timeout = TimeSpan.FromSeconds(10) };

        var error = await Assert.ThrowsAsync<MailConnectionException>(
            () => strict.VerifyAsync(Account(), _imap.Password, TestContext.Current.CancellationToken));

        Assert.Equal(MailError.Certificate, error.Error);
        Assert.DoesNotContain("AUTHENTICATE", _imap.Commands);
    }

    [Fact]
    public async Task An_unknown_host_is_reported_as_such()
    {
        var account = Account() with { Imap = new MailServer { Host = "serveur-inexistant.invalid", Port = 993 } };

        var error = await Assert.ThrowsAsync<MailConnectionException>(
            () => _service.VerifyAsync(account, "x", TestContext.Current.CancellationToken));

        Assert.Equal(MailError.HostNotFound, error.Error);
    }

    [Fact]
    public async Task A_closed_port_is_a_connection_failure()
    {
        var account = Account() with { Imap = new MailServer { Host = "localhost", Port = ClosedPort() } };

        var error = await Assert.ThrowsAsync<MailConnectionException>(
            () => _service.VerifyAsync(account, "x", TestContext.Current.CancellationToken));

        Assert.Equal(MailError.ConnectionFailed, error.Error);
    }

    [Fact]
    public async Task Folders_come_with_their_role_depth_and_counts()
    {
        var account = Stored();

        var folders = await _service.GetFoldersAsync(account, TestContext.Current.CancellationToken);

        Assert.Equal(["INBOX", "Brouillons", "Envoyés", "Corbeille", "Archives", "Archives/2026"], folders.Select(f => f.FullName));
        var inbox = folders[0];
        Assert.Equal(FolderRole.Inbox, inbox.Role);
        Assert.Equal(120, inbox.Total);
        Assert.Equal(80, inbox.Unread);
        Assert.Equal(FolderRole.Drafts, folders[1].Role);
        Assert.Equal(FolderRole.Sent, folders[2].Role);
        Assert.Equal(FolderRole.Trash, folders[3].Role);
        Assert.False(folders[4].CanSelect);
        Assert.Equal(1, folders[5].Depth);
        Assert.Equal("2026", folders[5].Name);
    }

    [Fact]
    public async Task Headers_come_by_pages_newest_first()
    {
        var account = Stored();

        var first = await _service.GetMessagesAsync(account, "INBOX", 0, 50, TestContext.Current.CancellationToken);
        var second = await _service.GetMessagesAsync(account, "INBOX", 50, 50, TestContext.Current.CancellationToken);
        var last = await _service.GetMessagesAsync(account, "INBOX", 100, 50, TestContext.Current.CancellationToken);
        var beyond = await _service.GetMessagesAsync(account, "INBOX", 120, 50, TestContext.Current.CancellationToken);

        Assert.Equal(120, first.Total);
        Assert.Equal(1u, first.UidValidity);
        Assert.Equal(Enumerable.Range(71, 50).Reverse().Select(i => (uint)i), first.Messages.Select(m => m.Uid));
        Assert.Equal(Enumerable.Range(21, 50).Reverse().Select(i => (uint)i), second.Messages.Select(m => m.Uid));
        Assert.Equal(Enumerable.Range(1, 20).Reverse().Select(i => (uint)i), last.Messages.Select(m => m.Uid));
        Assert.Empty(beyond.Messages);

        var newest = first.Messages[0];
        Assert.Equal("Message 120", newest.Subject);
        Assert.Equal("Expéditeur 120", newest.FromName);
        Assert.Equal("expediteur120@example.org", newest.FromAddress);
        Assert.Equal(Newest, newest.Date);
        Assert.True(newest.IsRead);
        Assert.False(first.Messages[1].IsRead);

        // Une seule connexion pour tout : elle est gardée ouverte par compte.
        Assert.Equal(1, _imap.ConnectionCount);
        Assert.DoesNotContain(_imap.Commands, c => c.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || c.StartsWith("STORE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_sub_folder_is_read_with_flags_and_non_ascii_subjects()
    {
        var account = Stored();

        var page = await _service.GetMessagesAsync(account, "Archives/2026", 0, 50, TestContext.Current.CancellationToken);

        var message = Assert.Single(page.Messages);
        Assert.Equal("Facture d'électricité", message.Subject);
        Assert.Equal("EDF", message.Sender);
        Assert.True(message.IsFlagged);
    }

    [Fact]
    public async Task Without_a_stored_password_nothing_is_attempted()
    {
        var account = Account();

        var error = await Assert.ThrowsAsync<MailConnectionException>(
            () => _service.GetFoldersAsync(account, TestContext.Current.CancellationToken));

        Assert.Equal(MailError.MissingPassword, error.Error);
        Assert.Equal(0, _imap.ConnectionCount);
    }

    [Fact]
    public async Task After_disconnecting_the_next_call_reconnects()
    {
        var account = Stored();
        await _service.GetFoldersAsync(account, TestContext.Current.CancellationToken);

        await _service.DisconnectAsync(account.Id);
        await _service.GetFoldersAsync(account, TestContext.Current.CancellationToken);

        Assert.Equal(2, _imap.ConnectionCount);
    }

    private Account Account() => new()
    {
        Address = "alice@example.org",
        UserName = _imap.UserName,
        Imap = _imap.Endpoint,
        Smtp = _smtp.Endpoint,
    };

    private Account Stored()
    {
        var account = Account();
        _credentials.SetPassword(account.Id, _imap.Password);
        return account;
    }

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
