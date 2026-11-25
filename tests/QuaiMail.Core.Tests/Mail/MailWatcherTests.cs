using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Mail;

/// <summary>Réception en direct contre le faux serveur local : IDLE, interrogation, reconnexion automatique.</summary>
public sealed class MailWatcherTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 30, 0, TimeSpan.Zero);

    private readonly FakeImapServer _imap = new();
    private readonly MemoryCredentialStore _credentials = new();
    private readonly List<NewMailEventArgs> _events = [];
    private MailKitMailWatcher _watcher = null!;
    private Account _account = null!;

    public ValueTask InitializeAsync()
    {
        _imap.Folders.Add(new FakeFolder("INBOX"));
        FakeImapServer.Fill(_imap.Folder("INBOX"), 3, Now);
        _account = new Account
        {
            Address = "alice@example.org",
            UserName = _imap.UserName,
            Imap = _imap.Endpoint,
            Smtp = new MailServer { Host = "localhost", Port = 1 },
        };
        _credentials.SetPassword(_account.Id, _imap.Password);
        _watcher = Watcher();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _watcher.DisposeAsync();
        await _imap.DisposeAsync();
    }

    [Fact]
    public async Task A_new_mail_is_announced_live_and_existing_ones_are_not()
    {
        _watcher.Watch([_account]);
        await WaitForAsync(() => _imap.Idling == 1);
        Assert.Empty(_events);

        await _imap.DeliverAsync("INBOX", new FakeMessage(4, "Tout frais", "Léa", "lea@example.org", Now.AddMinutes(1)));
        await WaitForAsync(() => _events.Count == 1);

        var arrived = Assert.Single(_events[0].Messages);
        Assert.Equal((4u, "Tout frais"), (arrived.Uid, arrived.Subject));
        Assert.Equal(_account, _events[0].Account);
        await WaitForAsync(() => _imap.Idling == 1);
    }

    [Fact]
    public async Task Mails_that_arrive_during_a_cut_are_announced_after_the_automatic_reconnection()
    {
        _watcher.Watch([_account]);
        await WaitForAsync(() => _imap.Idling == 1);

        _imap.DropConnections();
        _imap.Folder("INBOX").Messages.Add(new FakeMessage(4, "Pendant la coupure", "Bob", "bob@example.org", Now.AddMinutes(2)));
        await WaitForAsync(() => _watcher.Connections >= 2 && _events.Count == 1);

        Assert.Equal("Pendant la coupure", Assert.Single(_events[0].Messages).Subject);
    }

    [Fact]
    public async Task Without_idle_the_server_is_polled()
    {
        _imap.SupportsIdle = false;
        _watcher.Watch([_account]);

        // Le mail arrive après la première synchronisation : seul un NOOP peut le faire découvrir.
        int Polls()
        {
            lock (_imap.Commands)
            {
                return _imap.Commands.Count(c => c == "NOOP");
            }
        }

        await WaitForAsync(() => Polls() > 0);
        var polls = Polls();
        _imap.Folder("INBOX").Messages.Add(new FakeMessage(4, "Relevé", "Banque", "banque@example.org", Now.AddMinutes(3)));
        await WaitForAsync(() => _events.Count == 1);

        Assert.True(Polls() > polls);
    }

    [Fact]
    public async Task A_refused_password_stops_watching()
    {
        _credentials.SetPassword(_account.Id, "faux");
        _watcher.Watch([_account]);
        await WaitForAsync(() => _imap.Commands.Contains("AUTHENTICATE"));

        // Plus d'une attente de reconnexion (100 ms ici) : aucune nouvelle tentative.
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.Equal(1, _imap.ConnectionCount);
        Assert.Equal(0, _watcher.Connections);
    }

    [Fact]
    public async Task An_account_removed_from_the_list_is_no_longer_watched()
    {
        _watcher.Watch([_account]);
        await WaitForAsync(() => _imap.Idling == 1);

        _watcher.Watch([]);
        await WaitForAsync(() => _imap.Idling == 0);
    }

    [Fact]
    public async Task The_main_connection_reconnects_once_by_itself_after_a_cut()
    {
        await using var service = new MailKitMailService(_credentials, null, TestCertificate.Pinned) { Timeout = TimeSpan.FromSeconds(10) };
        await service.GetFoldersAsync(_account, TestContext.Current.CancellationToken);

        _imap.DropConnections();
        var folders = await service.GetFoldersAsync(_account, TestContext.Current.CancellationToken);

        Assert.Contains(folders, f => f.FullName == "INBOX");
        Assert.Equal(2, _imap.ConnectionCount);
    }

    private MailKitMailWatcher Watcher()
    {
        var watcher = new MailKitMailWatcher(_credentials, null, TestCertificate.Pinned)
        {
            Backoff = [TimeSpan.FromMilliseconds(100)],
            PollInterval = TimeSpan.FromMilliseconds(200),
            Timeout = TimeSpan.FromSeconds(10),
        };
        watcher.NewMail += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
        return watcher;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition non atteinte en 15 s");
            await Task.Delay(20);
        }
    }
}
