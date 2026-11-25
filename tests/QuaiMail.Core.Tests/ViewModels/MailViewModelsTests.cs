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
    public async Task Opening_a_message_shows_it_and_marks_it_read_everywhere()
    {
        var (mailbox, account) = await OpenInboxAsync();
        var reader = mailbox.Reader;
        var unreadBefore = mailbox.Items[1].Unread;
        var message = mailbox.Messages.Messages.First(m => !m.IsRead);

        mailbox.Messages.SelectedMessage = message;
        await WaitForAsync(() => reader.HasMessage && !reader.IsLoading && reader.IsRead);

        Assert.Equal(message.Subject, reader.Subject);
        Assert.Equal("Bob <bob@example.org>", reader.From);
        Assert.Contains($"Corps {message.Uid}", reader.Page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", reader.Page, StringComparison.Ordinal);
        Assert.Contains((message.Uid, (bool?)true, (bool?)null), _mail.FlagChanges);
        Assert.True(message.IsRead);
        Assert.Equal(unreadBefore - 1, mailbox.Items[1].Unread);
        Assert.True(_cache.GetMessages(account.Id, "INBOX", 0, 10).Single(m => m.Uid == message.Uid).IsRead);
        Assert.Equal(message.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture), _shell.OpenMessageId);
    }

    [Fact]
    public async Task Read_and_star_can_be_toggled()
    {
        var (mailbox, _) = await OpenInboxAsync();
        var reader = mailbox.Reader;
        var message = mailbox.Messages.Messages.First(m => m.IsRead);
        mailbox.Messages.SelectedMessage = message;
        await WaitForAsync(() => reader.HasMessage && !reader.IsLoading);
        var unread = mailbox.Items[1].Unread;

        await reader.ToggleReadCommand.ExecuteAsync(null);
        await reader.ToggleFlagCommand.ExecuteAsync(null);

        Assert.False(reader.IsRead);
        Assert.True(reader.IsFlagged);
        Assert.False(message.IsRead);
        Assert.True(message.IsFlagged);
        Assert.Equal(unread + 1, mailbox.Items[1].Unread);
        Assert.Equal([(message.Uid, (bool?)false, (bool?)null), (message.Uid, null, true)], _mail.FlagChanges);
    }

    [Fact]
    public async Task Remote_images_wait_for_the_reader_to_ask()
    {
        var (mailbox, _) = await OpenInboxAsync();
        _mail.Contents[3] = Html(3, "<p>Promo</p><img src=\"https://tracker.example/pixel.gif\"><img src=\"https://cdn.example/photo.jpg\">");
        var reader = mailbox.Reader;
        var pages = 0;
        reader.PageChanged += (_, _) => pages++;

        mailbox.Messages.SelectedMessage = mailbox.Messages.Messages.Single(m => m.Uid == 3);
        await WaitForAsync(() => !reader.IsLoading && reader.Content is not null);

        Assert.Equal(2, reader.BlockedImages);
        Assert.True(reader.OffersImages);
        Assert.DoesNotContain("tracker.example", reader.Page, StringComparison.Ordinal);

        reader.ShowImagesCommand.Execute(null);

        Assert.Contains("https://tracker.example/pixel.gif", reader.Page, StringComparison.Ordinal);
        Assert.Contains(QuaiMail.Core.Reading.ReaderPage.RemoteImageSource, reader.Page, StringComparison.Ordinal);
        Assert.False(reader.OffersImages);
        Assert.True(pages >= 2);
    }

    [Fact]
    public async Task With_the_setting_off_remote_images_are_shown_directly()
    {
        _settings.Update(s => s.BlockRemoteImages = false);
        var (mailbox, _) = await OpenInboxAsync();
        _mail.Contents[3] = Html(3, "<img src=\"https://cdn.example/photo.jpg\">");

        mailbox.Messages.SelectedMessage = mailbox.Messages.Messages.Single(m => m.Uid == 3);
        await WaitForAsync(() => !mailbox.Reader.IsLoading && mailbox.Reader.Content is not null);

        Assert.Contains("https://cdn.example/photo.jpg", mailbox.Reader.Page, StringComparison.Ordinal);
        Assert.Equal(0, mailbox.Reader.BlockedImages);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    public async Task Other_link_schemes_are_refused_without_asking(string url)
    {
        var (mailbox, _) = await OpenInboxAsync();

        Assert.False(await mailbox.Reader.OpenLinkAsync(new Uri(url)));

        Assert.Empty(_dialogs.Confirmations);
        Assert.Empty(Launcher.Opened);
    }

    [Fact]
    public async Task A_link_opens_in_the_browser_only_after_showing_its_real_address()
    {
        var (mailbox, _) = await OpenInboxAsync();
        var link = new Uri("https://evil.example/connexion?banque=1");

        _dialogs.ConfirmAnswer = false;
        Assert.False(await mailbox.Reader.OpenLinkAsync(link));
        Assert.Empty(Launcher.Opened);

        _dialogs.ConfirmAnswer = true;
        Assert.True(await mailbox.Reader.OpenLinkAsync(link));
        Assert.Equal([link], Launcher.Opened);
        Assert.All(_dialogs.Confirmations, c => Assert.Equal("Link_Message(https://evil.example/connexion?banque=1,evil.example)", c.Message));
    }

    [Fact]
    public async Task Attachments_are_saved_atomically_and_executables_need_a_second_thought()
    {
        var (mailbox, _) = await OpenInboxAsync();
        var builder = new MimeKit.BodyBuilder { TextBody = "Voir pièces jointes" };
        builder.Attachments.Add("facture.pdf", "contenu PDF"u8.ToArray());
        builder.Attachments.Add("outil.exe", "MZ binaire"u8.ToArray());
        _mail.Contents[3] = new MimeKit.MimeMessage([new MimeKit.MailboxAddress("Bob", "bob@example.org")], [], "Pièces jointes", builder.ToMessageBody());
        var reader = mailbox.Reader;
        mailbox.Messages.SelectedMessage = mailbox.Messages.Messages.Single(m => m.Uid == 3);
        await WaitForAsync(() => !reader.IsLoading && reader.HasAttachments);

        Assert.Equal(["facture.pdf", "outil.exe"], reader.Attachments.Select(a => a.FileName));
        Assert.Equal(11, reader.Attachments[0].Size);
        Assert.False(reader.Attachments[0].IsExecutable);
        Assert.True(reader.Attachments[1].IsExecutable);

        var target = Path.Combine(_temp.Path, "facture.pdf");
        Files.Answer = target;
        Assert.Equal(target, await reader.SaveAttachmentAsync(reader.Attachments[0]));
        Assert.Equal("contenu PDF", File.ReadAllText(target));
        Assert.Empty(_dialogs.Confirmations);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));

        _dialogs.ConfirmAnswer = false;
        Files.Answer = Path.Combine(_temp.Path, "outil.exe");
        Assert.Null(await reader.SaveAttachmentAsync(reader.Attachments[1]));
        Assert.True(Assert.Single(_dialogs.Confirmations).Destructive);
        Assert.False(File.Exists(Files.Answer));
        Assert.Equal(["facture.pdf"], Files.Suggested);
        Assert.Empty(Launcher.Opened);
    }

    [Fact]
    public async Task A_plain_text_mail_is_shown_with_clickable_links_and_nothing_active()
    {
        var (mailbox, _) = await OpenInboxAsync();
        _mail.Contents[3] = new MimeKit.MimeMessage([new MimeKit.MailboxAddress("Bob", "bob@example.org")], [], "Texte",
            new MimeKit.TextPart("plain") { Text = "Bonjour <script>alert(1)</script>\nVoir https://example.org/page" });

        mailbox.Messages.SelectedMessage = mailbox.Messages.Messages.Single(m => m.Uid == 3);
        await WaitForAsync(() => !mailbox.Reader.IsLoading && mailbox.Reader.Content is not null);

        Assert.Contains("href=\"https://example.org/page\"", mailbox.Reader.Page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", mailbox.Reader.Page, StringComparison.Ordinal);
        Assert.Contains("mail-plain", mailbox.Reader.Page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_message_open_before_an_update_is_opened_again()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 3)];
        _mail.Messages["INBOX"] = [Header(1), Header(2), Header(3)];
        _shell.SelectedAccountId = account.Id;
        _shell.SelectedFolderPath = "INBOX";
        _shell.OpenMessageId = "2";
        var mailbox = Mailbox();

        await mailbox.LoadAsync();
        await WaitForAsync(() => mailbox.Reader.Content is { Uid: 2 });

        Assert.Equal(2u, mailbox.Messages.SelectedMessage?.Uid);
    }

    [Fact]
    public async Task Another_folder_closes_the_open_message()
    {
        var (mailbox, account) = await OpenInboxAsync();
        _mail.Folders[account.Id].Add(new MailFolderInfo("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 0));
        await mailbox.RefreshAsync();
        mailbox.Messages.SelectedMessage = mailbox.Messages.Messages[0];
        await WaitForAsync(() => mailbox.Reader.HasMessage && !mailbox.Reader.IsLoading);

        mailbox.SelectedItem = mailbox.Items.Single(i => i.Folder?.FullName == "Envoyés");

        Assert.False(mailbox.Reader.HasMessage);
        Assert.Null(mailbox.Messages.SelectedMessage);
    }

    private async Task<(MailboxViewModel Mailbox, Account Account)> OpenInboxAsync()
    {
        var account = AddAccount("alice@example.org");
        _mail.Folders[account.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 2, 4)];
        _mail.Messages["INBOX"] = [Header(1), Header(2), Header(3), Header(4)];
        var mailbox = Mailbox();
        await mailbox.LoadAsync();
        await WaitForAsync(() => !mailbox.Messages.IsLoading && mailbox.Messages.Messages.Count == 4);
        return (mailbox, account);
    }

    private static MimeKit.MimeMessage Html(uint uid, string html) =>
        new([new MimeKit.MailboxAddress("Bob", "bob@example.org")], [], $"Objet {uid}", new MimeKit.TextPart("html") { Text = html });

    [Fact]
    public async Task Deleting_goes_to_the_trash_first_and_leaves_the_list()
    {
        var (mailbox, account) = await OpenInboxAsync();
        _mail.Folders[account.Id].Add(new MailFolderInfo("Corbeille", "Corbeille", 0, FolderRole.Trash, true, 0, 0));
        await mailbox.RefreshAsync();
        var message = mailbox.Messages.Messages.First(m => !m.IsRead);
        var unread = mailbox.Items.Single(i => i.Role == FolderRole.Inbox).Unread;
        mailbox.Messages.SelectedMessage = message;
        await WaitForAsync(() => mailbox.Reader.HasMessage && !mailbox.Reader.IsLoading);
        mailbox.Reader.ToggleReadCommand.Execute(null);
        await WaitForAsync(() => !message.IsRead);

        Assert.True(await mailbox.DeleteAsync(message));

        Assert.Equal([(message.Uid, "INBOX", "Corbeille")], _mail.Moves);
        Assert.Empty(_dialogs.Confirmations);
        Assert.DoesNotContain(message, mailbox.Messages.Messages);
        Assert.False(mailbox.Reader.HasMessage);
        Assert.Equal(unread, mailbox.Items.Single(i => i.Role == FolderRole.Inbox).Unread + 1);
        Assert.Null(_cache.GetMessages(account.Id, "INBOX", 0, 10).FirstOrDefault(m => m.Uid == message.Uid));
    }

    [Fact]
    public async Task Without_a_trash_deleting_for_good_needs_a_confirmation()
    {
        var (mailbox, _) = await OpenInboxAsync();
        _mail.Trash = null;
        var message = mailbox.Messages.Messages[0];

        _dialogs.ConfirmAnswer = false;
        Assert.False(await mailbox.DeleteAsync(message));
        Assert.Contains(message, mailbox.Messages.Messages);
        Assert.Empty(_mail.PermanentlyDeleted);
        Assert.True(Assert.Single(_dialogs.Confirmations).Destructive);

        _dialogs.ConfirmAnswer = true;
        Assert.True(await mailbox.DeleteAsync(message));
        Assert.Equal([("INBOX", message.Uid)], _mail.PermanentlyDeleted);
        Assert.DoesNotContain(message, mailbox.Messages.Messages);
    }

    [Fact]
    public async Task A_message_can_be_moved_to_another_folder_of_its_account()
    {
        var (mailbox, account) = await OpenInboxAsync();
        _mail.Folders[account.Id].Add(new MailFolderInfo("Archives", "Archives", 0, FolderRole.Archive, true, 0, 0));
        await mailbox.RefreshAsync();
        var message = mailbox.Messages.Messages[0];

        var targets = mailbox.MoveTargets(message);
        Assert.Equal(["Archives"], targets.Select(t => t.Folder!.FullName));
        Assert.True(await mailbox.MoveAsync(message, targets[0]));

        Assert.Equal([(message.Uid, "INBOX", "Archives")], _mail.Moves);
        Assert.DoesNotContain(message, mailbox.Messages.Messages);
    }

    [Fact]
    public async Task Search_finds_cached_mails_ignoring_accents_and_case_then_on_the_server()
    {
        var (mailbox, account) = await OpenInboxAsync();
        _cache.SaveMessages(account.Id, "Archives", 1, [new MessageHeader(7, "Réunion budgétaire", "Léa", "lea@example.org", Now.AddDays(-40), true, false, 10)]);
        var list = mailbox.Messages;

        await list.SearchAsync("reunion BUDGET");

        Assert.True(list.IsSearching);
        Assert.Equal("Search_Title(reunion BUDGET)", list.Title);
        var hit = Assert.Single(list.Messages);
        Assert.Equal(("Archives", 7u, account.Id), (hit.Folder, hit.Uid, hit.AccountId));

        // Ouvrir un résultat ouvre bien le mail de son dossier.
        list.SelectedMessage = hit;
        await WaitForAsync(() => mailbox.Reader.Content is { Uid: 7 });
        Assert.Contains("message Archives 7", _mail.Calls);

        // Le texte d'un mail lu devient cherchable.
        await list.SearchAsync("Corps");
        Assert.Contains(list.Messages, m => m.Uid == 7);

        _mail.ServerSearchResults.Add(new MessageHeader(999, "Trouvé sur le serveur", "Bob", "bob@example.org", Now, false, false, 10));
        await list.SearchServerCommand.ExecuteAsync(null);
        Assert.Contains("search INBOX Corps", _mail.Calls);
        Assert.Contains(list.Messages, m => m.Uid == 999 && m.Folder == "INBOX");

        await list.EndSearchAsync();
        await WaitForAsync(() => !list.IsLoading);
        Assert.False(list.IsSearching);
        Assert.Equal("Folder_Inbox", list.Title);
        Assert.Equal(4, list.Messages.Count);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("facture", "\"facture\"*")]
    [InlineData("a\"b OR c*", "\"a\"\"b\"* \"OR\"* \"c\"*")]
    [InlineData("subject:(x) NEAR", "\"subject:(x\"* \"NEAR\"*")]
    public void Search_text_is_never_interpreted_as_fts_syntax(string text, string expected) =>
        Assert.Equal(expected, QuaiMail.Core.Cache.MailCache.FtsQuery(text));

    [Fact]
    public async Task With_several_accounts_a_unified_inbox_mixes_them_by_date()
    {
        var alice = AddAccount("alice@example.org");
        var bob = AddAccount("bob@example.org");
        _mail.Folders[alice.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 2, 60)];
        _mail.Folders[bob.Id] = [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 1, 60)];
        // Le faux service sert la même boîte « INBOX » aux deux comptes : 60 mails, un par heure (UID croissant = plus récent).
        _mail.Messages["INBOX"] = [.. Enumerable.Range(1, 60).Select(i => Header((uint)i) with { Date = Now.AddHours(i - 60) })];
        var mailbox = Mailbox();

        await mailbox.LoadAsync();
        await WaitForAsync(() => !mailbox.Messages.IsLoading && mailbox.Messages.Messages.Count == MessageListViewModel.PageSize);

        var unified = mailbox.Items[0];
        Assert.True(unified.IsUnified);
        Assert.Equal("Folder_Unified", unified.Title);
        Assert.Equal(3, unified.Unread);
        Assert.Same(unified, mailbox.SelectedItem);
        Assert.Equal(NavItemViewModel.UnifiedId, _shell.SelectedAccountId);
        var list = mailbox.Messages.Messages;
        Assert.True(list.Zip(list.Skip(1)).All(p => p.First.Header.Date >= p.Second.Header.Date));
        Assert.Equal(2, list.Select(m => m.AccountId).Distinct().Count());
        Assert.True(mailbox.Messages.HasMore);

        await mailbox.Messages.LoadMoreCommand.ExecuteAsync(null);
        await mailbox.Messages.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(120, list.Count);
        Assert.False(mailbox.Messages.HasMore);
        Assert.Equal(60, list.Count(m => m.AccountId == alice.Id));
    }

    [Fact]
    public async Task New_mail_arrives_on_top_of_the_inbox_shown_and_in_the_counters()
    {
        var (mailbox, account) = await OpenInboxAsync();
        var inbox = mailbox.Items.Single(i => i.Role == FolderRole.Inbox);
        var unread = inbox.Unread;
        var fresh = new MessageHeader(50, "Tout frais", "Léa", "lea@example.org", Now.AddMinutes(5), false, false, 10);

        var notified = mailbox.OnNewMail(_accounts.Find(account.Id)!, [fresh]);

        Assert.Equal([fresh], notified);
        Assert.Equal(50u, mailbox.Messages.Messages[0].Uid);
        Assert.Equal(unread + 1, inbox.Unread);

        // Déjà là : pas de doublon.
        mailbox.OnNewMail(_accounts.Find(account.Id)!, [fresh]);
        Assert.Single(mailbox.Messages.Messages, m => m.Uid == 50);
    }

    [Fact]
    public async Task New_mail_does_not_disturb_another_folder_shown()
    {
        var (mailbox, account) = await OpenInboxAsync();
        _mail.Folders[account.Id].Add(new MailFolderInfo("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 0));
        await mailbox.RefreshAsync();
        mailbox.SelectedItem = mailbox.Items.Single(i => i.Folder?.FullName == "Envoyés");
        await WaitForAsync(() => !mailbox.Messages.IsLoading);

        mailbox.OnNewMail(_accounts.Find(account.Id)!, [new MessageHeader(50, "Tout frais", "Léa", "lea@example.org", Now, false, false, 10)]);

        Assert.DoesNotContain(mailbox.Messages.Messages, m => m.Uid == 50);
        Assert.Equal(3, mailbox.Items.Single(i => i.Role == FolderRole.Inbox).Unread);
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

    private readonly JsonSettingsService _settings = new(Path.Combine(Path.GetTempPath(), "quai-mail-tests-" + Guid.NewGuid().ToString("N"), "settings.json"));

    internal FakeFileDialogs Files { get; } = new();

    internal FakeLauncher Launcher { get; } = new();

    private MailboxViewModel Mailbox() =>
        new(_accounts, _credentials, _mail, _cache, _shell, new MessageListViewModel(_mail, _cache, new KeyStrings(), new FixedTime(Now)), Reader(), _dialogs, new KeyStrings());

    private ReaderViewModel Reader() =>
        new(_mail, _settings, _dialogs, Files, Launcher, new KeyStrings(), new QuaiMail.Core.Reading.ReaderTemplate(File.ReadAllText(Reading.HtmlCleanerTests.TemplatePath())), null, _cache);

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
