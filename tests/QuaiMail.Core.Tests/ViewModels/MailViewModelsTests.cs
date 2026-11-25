using Microsoft.Data.Sqlite;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.ViewModels;

public sealed class MailViewModelsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly AccountStore _accounts;
    private readonly MemoryCredentialStore _credentials = new();
    private readonly FakeMailService _mail = new();
    private readonly MailCache _cache;
    private readonly FakeDialogService _dialogs = new();
    private readonly ShellViewModel _shell;

    public MailViewModelsTests()
    {
        _paths = new AppPaths(_temp.Path);
        _accounts = new AccountStore(_paths);
        _cache = new MailCache(_paths.CacheDatabase);
        _shell = new ShellViewModel(_dialogs, new KeyStrings(), new DraftStore(_paths), new WindowStateStore(_paths));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public async Task Without_account_the_pane_says_so()
    {
        var mailbox = Mailbox();

        await mailbox.LoadAsync();

        Assert.True(mailbox.HasNoAccount);
        Assert.Empty(mailbox.Items);
        Assert.Null(mailbox.SelectedItem);
    }

    [Fact]
    public async Task Accounts_and_folders_are_listed_and_the_first_inbox_is_opened()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] =
        [
            new MailFolderInfo("Archives/2026", "2026", 1, FolderRole.None, true, 0, 1),
            new MailFolderInfo("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 2),
            new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 2, 3),
        ];
        _mail.Messages["INBOX"] = [Header(1), Header(2), Header(3)];
        var mailbox = Mailbox();

        await mailbox.LoadAsync();
        await WaitForAsync(() => !mailbox.Messages.IsLoading && mailbox.Messages.Messages.Count == 3);

        Assert.True(mailbox.HasAccounts);
        Assert.Equal(["alice@example.org", "Folder_Inbox", "Folder_Sent", "2026"], mailbox.Items.Select(i => i.Title));
        Assert.True(mailbox.Items[0].IsAccount);
        Assert.Equal(2, mailbox.Items[1].Unread);
        Assert.Same(mailbox.Items[1], mailbox.SelectedItem);
        Assert.Equal(account.Id, _shell.SelectedAccountId);
        Assert.Equal("INBOX", _shell.SelectedFolderPath);
        Assert.Equal([3u, 2u, 1u], mailbox.Messages.Messages.Select(m => m.Uid));
        Assert.Equal("Folder_Inbox", mailbox.Messages.Title);

        // Le cache a été rempli : sans serveur, le volet se remplit quand même.
        Assert.Equal(3, _cache.GetFolders(account.Id).Count);
        Assert.Equal(3, _cache.GetMessages(account.Id, "INBOX", 0, 10).Count);
    }

    [Fact]
    public async Task The_folder_noted_before_an_update_is_selected_again()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] =
        [
            new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 0),
            new MailFolderInfo("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 0),
        ];
        _cache.SaveFolders(account.Id, _mail.Folders[account.Id]);
        _shell.SelectedAccountId = account.Id;
        _shell.SelectedFolderPath = "Envoyés";
        var mailbox = Mailbox();

        await mailbox.LoadAsync();

        Assert.Equal("Envoyés", mailbox.SelectedItem?.Folder?.FullName);
        Assert.Equal("Envoyés", _shell.SelectedFolderPath);
    }

    [Fact]
    public async Task Choosing_an_account_line_opens_its_inbox()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 0), new MailFolderInfo("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 0)];
        var mailbox = Mailbox();
        await mailbox.LoadAsync();
        mailbox.SelectedItem = mailbox.Items[2];

        mailbox.SelectedItem = mailbox.Items[0];

        Assert.Equal("INBOX", mailbox.SelectedItem?.Folder?.FullName);
    }

    [Fact]
    public async Task An_unreachable_server_shows_the_cache_and_a_clear_error()
    {
        var account = AddAccount("alice@example.org");
        _cache.SaveFolders(account.Id, [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 1, 1)]);
        _cache.SaveMessages(account.Id, "INBOX", 1, [Header(9)]);
        _mail.Failure = new MailConnectionException(MailError.HostNotFound, "imap.example.org:993", "IMAP");
        var mailbox = Mailbox();

        await mailbox.LoadAsync();
        await WaitForAsync(() => !mailbox.Messages.IsLoading);

        Assert.Equal(2, mailbox.Items.Count);
        Assert.Equal([9u], mailbox.Messages.Messages.Select(m => m.Uid));
        Assert.Equal("Mailbox_AccountError(alice@example.org,MailError_Format(IMAP,imap.example.org:993,MailError_HostNotFound))", mailbox.ErrorMessage);
        Assert.True(mailbox.Messages.HasError);
    }

    [Fact]
    public async Task Older_messages_come_page_after_page()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 120)];
        _mail.Messages["INBOX"] = [.. Enumerable.Range(1, 120).Select(i => Header((uint)i))];
        var mailbox = Mailbox();
        await mailbox.LoadAsync();
        var list = mailbox.Messages;
        await WaitForAsync(() => !list.IsLoading && list.Messages.Count == 50);

        Assert.True(list.HasMore);
        Assert.True(list.LoadMoreCommand.CanExecute(null));
        await list.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(100, list.Messages.Count);
        await list.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(120, list.Messages.Count);
        Assert.False(list.HasMore);
        Assert.Equal(Enumerable.Range(1, 120).Reverse().Select(i => (uint)i), list.Messages.Select(m => m.Uid));
        Assert.Equal(["messages INBOX 0", "messages INBOX 50", "messages INBOX 100"], _mail.Calls.Where(c => c.StartsWith("messages", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Choosing_another_folder_while_loading_never_mixes_the_lists()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 1), new MailFolderInfo("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 1)];
        _mail.Messages["INBOX"] = [Header(1)];
        _mail.Messages["Envoyés"] = [Header(2)];
        _mail.Gate = new TaskCompletionSource();
        var mailbox = Mailbox();
        var loading = mailbox.LoadAsync();

        await WaitForAsync(() => mailbox.Items.Count == 3 && mailbox.Messages.IsLoading);
        mailbox.SelectedItem = mailbox.Items[2];
        _mail.Gate.SetResult();
        await loading;
        await WaitForAsync(() => !mailbox.Messages.IsLoading);

        Assert.Equal([2u], mailbox.Messages.Messages.Select(m => m.Uid));
        Assert.Equal("Envoyés", _shell.SelectedFolderPath);
    }

    [Fact]
    public async Task Removing_an_account_asks_first_then_forgets_everything_local()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 0)];
        var mailbox = Mailbox();
        await mailbox.LoadAsync();

        _dialogs.ConfirmAnswer = false;
        Assert.False(await mailbox.RemoveAccountAsync(account.Id));
        Assert.Single(_accounts.All);

        _dialogs.ConfirmAnswer = true;
        Assert.True(await mailbox.RemoveAccountAsync(account.Id));

        Assert.Empty(_accounts.All);
        Assert.Null(_credentials.GetPassword(account.Id));
        Assert.Empty(_cache.GetFolders(account.Id));
        Assert.Contains("disconnect " + account.Id, _mail.Calls);
        Assert.True(mailbox.HasNoAccount);
        Assert.Null(_shell.SelectedAccountId);
    }

    [Fact]
    public async Task Clearing_the_cache_rebuilds_everything_from_the_server()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 2)];
        _mail.Messages["INBOX"] = [Header(1), Header(2)];
        var mailbox = Mailbox();
        await mailbox.LoadAsync();
        await WaitForAsync(() => !mailbox.Messages.IsLoading && mailbox.Messages.Messages.Count == 2);

        await mailbox.ClearCacheAsync();
        await WaitForAsync(() => !mailbox.Messages.IsLoading);

        Assert.Equal(2, mailbox.Items.Count);
        Assert.Equal([2u, 1u], mailbox.Messages.Messages.Select(m => m.Uid));
        Assert.Equal(2, _cache.GetMessages(account.Id, "INBOX", 0, 10).Count);
    }

    [Fact]
    public void Dates_are_short_and_readable()
    {
        var now = new DateTimeOffset(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);
        using var culture = new CultureScope("fr-FR");

        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 5, 0, TimeSpan.Zero).ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture),
            MessageItemViewModel.ShortDate(new DateTimeOffset(2026, 11, 25, 9, 5, 0, TimeSpan.Zero), now));
        Assert.Equal("3 mars", MessageItemViewModel.ShortDate(new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero), now));
        Assert.Equal("03/03/2025", MessageItemViewModel.ShortDate(new DateTimeOffset(2025, 3, 3, 12, 0, 0, TimeSpan.Zero), now));
        Assert.Equal(string.Empty, MessageItemViewModel.ShortDate(DateTimeOffset.MinValue, now));
    }

    private MailboxViewModel Mailbox() =>
        new(_accounts, _credentials, _mail, _cache, _shell, new MessageListViewModel(_mail, _cache, new KeyStrings(), new FixedTime(Now)), _dialogs, new KeyStrings());

    private Account AddAccount(string address)
    {
        var account = new Account
        {
            Address = address,
            UserName = address,
            Imap = new MailServer { Host = "imap.example.org", Port = 993 },
            Smtp = new MailServer { Host = "smtp.example.org", Port = 465 },
        };
        _accounts.Save(account);
        _credentials.SetPassword(account.Id, "secret");
        return account;
    }

    private static MessageHeader Header(uint uid) =>
        new(uid, $"Objet {uid}", "Bob", "bob@example.org", Now.AddHours(-uid), uid % 2 == 0, false, 1000);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition non atteinte en 10 s");
            await Task.Delay(10);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
