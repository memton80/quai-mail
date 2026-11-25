using QuaiMail.Core.Models;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Storage;

public sealed class WindowStateStoreTests
{
    [Fact]
    public void The_state_is_written_in_the_updates_folder_and_restored_once()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new WindowStateStore(paths);
        var state = new WindowState { AccountId = "compte-1", FolderPath = "INBOX", MessageId = "42", DraftId = "0123456789abcdef0123456789abcdef" };

        store.SaveForUpdate(state);

        Assert.Equal(Path.Combine(temp.Path, "updates", "reopen.txt"), store.FilePath);
        Assert.True(File.Exists(paths.ReopenAfterUpdateFile));
        Assert.Equal(state, store.TakeAfterUpdate());
        Assert.False(File.Exists(paths.ReopenAfterUpdateFile));
        Assert.Null(store.TakeAfterUpdate());
    }

    [Fact]
    public void Only_identifiers_are_written()
    {
        using var temp = new TempDirectory();
        var store = new WindowStateStore(temp.File("reopen.txt"));

        store.SaveForUpdate(new WindowState { AccountId = "compte-1", FolderPath = "Archives/2026" });

        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"accountId\": \"compte-1\"", json);
        Assert.Contains("\"folderPath\": \"Archives/2026\"", json);
        Assert.DoesNotContain("isEmpty", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nothing_to_restore_without_a_file()
    {
        using var temp = new TempDirectory();

        Assert.Null(new WindowStateStore(temp.File("reopen.txt")).TakeAfterUpdate());
    }

    [Fact]
    public void An_unreadable_file_is_dropped_without_error()
    {
        using var temp = new TempDirectory();
        var path = temp.File("reopen.txt");
        File.WriteAllText(path, "{ pas du json");

        Assert.Null(new WindowStateStore(path).TakeAfterUpdate());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_newer_state_replaces_the_older_one()
    {
        using var temp = new TempDirectory();
        var store = new WindowStateStore(temp.File("updates/reopen.txt"));

        store.SaveForUpdate(new WindowState { FolderPath = "INBOX" });
        store.SaveForUpdate(new WindowState { FolderPath = "Envoyés" });

        Assert.Equal("Envoyés", store.TakeAfterUpdate()!.FolderPath);
    }
}
