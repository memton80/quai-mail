using QuaiMail.Core.Drafts;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Drafts;

public sealed class DraftStoreTests
{
    [Fact]
    public void A_saved_draft_is_read_back_identical()
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));
        var draft = new Draft { AccountId = "compte-1", To = "a@example.org", Subject = "Réunion", Body = "Bonjour,\n\nÀ demain." };

        store.Save(draft);
        var read = store.Load(draft.Id);

        Assert.NotNull(read);
        Assert.Equal(draft.Id, read.Id);
        Assert.Equal("compte-1", read.AccountId);
        Assert.Equal("a@example.org", read.To);
        Assert.Equal("Réunion", read.Subject);
        Assert.Equal("Bonjour,\n\nÀ demain.", read.Body);
        Assert.NotEqual(default, read.UpdatedAt);
    }

    [Fact]
    public void Saving_again_replaces_the_file_without_leaving_temporary_files()
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));
        var draft = new Draft { Subject = "v1" };

        store.Save(draft);
        draft.Subject = "v2";
        store.Save(draft);

        Assert.Equal("v2", store.Load(draft.Id)!.Subject);
        Assert.Single(Directory.GetFiles(store.Directory));
    }

    [Fact]
    public void A_missing_or_unreadable_draft_gives_null()
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));
        var id = Draft.NewId();

        Assert.Null(store.Load(id));

        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(Path.Combine(store.Directory, id + ".json"), "{ pas du json");
        Assert.Null(store.Load(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../settings")]
    [InlineData("..\\settings")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void Ids_that_are_not_ours_never_reach_the_disk(string id)
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));

        Assert.False(Draft.IsValidId(id));
        Assert.Null(store.Load(id));
        Assert.False(store.Delete(id));
        Assert.Throws<ArgumentException>(() => store.Save(new Draft { Id = id }));
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));
        var draft = new Draft { Subject = "À supprimer" };
        store.Save(draft);

        Assert.True(store.Delete(draft.Id));
        Assert.Null(store.Load(draft.Id));
        Assert.False(store.Delete(draft.Id));
    }

    [Fact]
    public void Drafts_are_listed_most_recent_first()
    {
        using var temp = new TempDirectory();
        var time = new StepTime();
        var store = new DraftStore(temp.File("drafts"), time);
        var first = new Draft { Subject = "premier" };
        var second = new Draft { Subject = "second" };

        store.Save(first);
        store.Save(second);

        Assert.Equal(["second", "premier"], store.List().Select(d => d.Subject));
    }

    [Fact]
    public void The_default_location_is_the_drafts_folder_of_the_data_directory()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "drafts"), new DraftStore(paths).Directory);
    }

    [Fact]
    public void An_empty_draft_has_nothing_to_keep()
    {
        Assert.True(new Draft().IsEmpty);
        Assert.True(new Draft { Subject = "  ", Body = "\n" }.IsEmpty);
        Assert.False(new Draft { Body = "x" }.IsEmpty);
        Assert.False(new Draft { To = "a@example.org" }.IsEmpty);
    }

    /// <summary>Horloge qui avance d'une minute à chaque lecture.</summary>
    private sealed class StepTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddMinutes(1);
    }
}
