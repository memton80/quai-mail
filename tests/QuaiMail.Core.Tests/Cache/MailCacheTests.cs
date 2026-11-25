using Microsoft.Data.Sqlite;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Cache;

public sealed class MailCacheTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly MailCache _cache;

    public MailCacheTests()
    {
        _cache = new MailCache(Path.Combine(_temp.Path, "cache", "mail.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Folders_are_kept_per_account_in_their_order()
    {
        var folders = new[]
        {
            new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 3, 10),
            new MailFolderInfo("Archives", "Archives", 0, FolderRole.None, false, 0, 0),
            new MailFolderInfo("Archives/2026", "2026", 1, FolderRole.None, true, 0, 4),
        };

        _cache.SaveFolders("a", folders);
        _cache.SaveFolders("b", [folders[0]]);

        Assert.Equal(folders, _cache.GetFolders("a"));
        Assert.Single(_cache.GetFolders("b"));
        Assert.Empty(_cache.GetFolders("c"));
    }

    [Fact]
    public void Headers_are_kept_newest_first_and_updated_in_place()
    {
        _cache.SaveMessages("a", "INBOX", 5, [Header(1), Header(2), Header(3)]);
        _cache.SaveMessages("a", "INBOX", 5, [Header(3) with { IsRead = true }, Header(4)]);

        var messages = _cache.GetMessages("a", "INBOX", 0, 10);

        Assert.Equal([4u, 3u, 2u, 1u], messages.Select(m => m.Uid));
        Assert.True(messages[1].IsRead);
        Assert.Equal(Header(4), messages[0]);
        Assert.Equal([2u, 1u], _cache.GetMessages("a", "INBOX", 2, 10).Select(m => m.Uid));
    }

    [Fact]
    public void A_new_uidvalidity_drops_the_old_headers()
    {
        _cache.SaveMessages("a", "INBOX", 5, [Header(1), Header(2)]);

        _cache.SaveMessages("a", "INBOX", 6, [Header(10)]);

        Assert.Equal([10u], _cache.GetMessages("a", "INBOX", 0, 10).Select(m => m.Uid));
    }

    [Fact]
    public void Removing_an_account_or_clearing_loses_nothing_that_the_server_does_not_have()
    {
        _cache.SaveFolders("a", [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 1)]);
        _cache.SaveMessages("a", "INBOX", 1, [Header(1)]);
        _cache.SaveMessages("b", "INBOX", 1, [Header(1)]);

        _cache.RemoveAccount("a");
        Assert.Empty(_cache.GetFolders("a"));
        Assert.Empty(_cache.GetMessages("a", "INBOX", 0, 10));
        Assert.Single(_cache.GetMessages("b", "INBOX", 0, 10));

        _cache.Clear();
        Assert.False(File.Exists(_cache.FilePath));
        Assert.Empty(_cache.GetMessages("b", "INBOX", 0, 10));
        _cache.SaveMessages("b", "INBOX", 1, [Header(2)]);
        Assert.Single(_cache.GetMessages("b", "INBOX", 0, 10));
    }

    [Fact]
    public void A_broken_or_older_file_is_simply_recreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cache.FilePath)!);
        File.WriteAllText(_cache.FilePath, "pas une base SQLite, mais du texte assez long pour ressembler à un fichier");

        _cache.SaveMessages("a", "INBOX", 1, [Header(1)]);

        Assert.Single(_cache.GetMessages("a", "INBOX", 0, 10));
    }

    [Fact]
    public void Folders_that_disappeared_take_their_headers_with_them()
    {
        _cache.SaveFolders("a", [new MailFolderInfo("Ancien", "Ancien", 0, FolderRole.None, true, 0, 1)]);
        _cache.SaveMessages("a", "Ancien", 1, [Header(1)]);

        _cache.SaveFolders("a", [new MailFolderInfo("INBOX", "INBOX", 0, FolderRole.Inbox, true, 0, 0)]);

        Assert.Empty(_cache.GetMessages("a", "Ancien", 0, 10));
    }

    private static MessageHeader Header(uint uid) =>
        new(uid, $"Objet {uid}", "Bob", "bob@example.org", new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).AddHours(uid), false, uid == 2, 1000 + uid);
}
