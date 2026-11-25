using QuaiMail.Core.Drafts;
using QuaiMail.Core.Models;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.ViewModels;

public sealed class ShellViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly FakeDialogService _dialogs = new();
    private readonly DraftStore _drafts;
    private readonly WindowStateStore _state;

    public ShellViewModelTests()
    {
        _paths = new AppPaths(_temp.Path);
        _drafts = new DraftStore(_paths);
        _state = new WindowStateStore(_paths);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Without_a_draft_the_update_goes_ahead_and_the_window_is_noted()
    {
        var shell = Create();
        shell.SelectedAccountId = "compte-1";
        shell.SelectedFolderPath = "INBOX";
        shell.OpenMessageId = "42";

        Assert.True(await shell.PrepareForUpdateAsync());

        Assert.Empty(_dialogs.AskedDrafts);
        Assert.Equal(new WindowState { AccountId = "compte-1", FolderPath = "INBOX", MessageId = "42" }, _state.TakeAfterUpdate());
    }

    [Fact]
    public async Task A_kept_draft_is_saved_locally_and_reopened_by_the_new_version()
    {
        var before = Create();
        before.SelectedAccountId = "compte-1";
        before.SelectedFolderPath = "INBOX";
        before.OpenMessageId = "42";
        var draft = new Draft { To = "a@example.org", Subject = "Devis", Body = "Bonjour" };
        before.OpenDraft(draft);
        _dialogs.DraftAnswers.Enqueue(DraftChoice.Keep);

        Assert.True(await before.PrepareForUpdateAsync());
        Assert.Equal(["Devis"], _dialogs.AskedDrafts);
        Assert.Equal("Devis", _drafts.Load(draft.Id)?.Subject);

        // Nouvelle version, lancée par l'installateur.
        var after = Create();
        Assert.True(after.RestoreAfterUpdate());

        Assert.Equal("compte-1", after.SelectedAccountId);
        Assert.Equal("INBOX", after.SelectedFolderPath);
        Assert.Equal("42", after.OpenMessageId);
        Assert.True(after.HasDraft);
        Assert.Equal(draft.Id, after.CurrentDraft!.Id);
        Assert.Equal("Bonjour", after.CurrentDraft.Body);
        Assert.Equal("Devis", after.DraftTitle);

        // Une seule fois : le fichier d'état est effacé.
        Assert.False(File.Exists(_paths.ReopenAfterUpdateFile));
        Assert.False(Create().RestoreAfterUpdate());
    }

    [Fact]
    public async Task Cancel_leaves_the_draft_open_and_notes_nothing()
    {
        var shell = Create();
        var draft = new Draft { Subject = "Pas fini" };
        shell.OpenDraft(draft);
        _dialogs.DraftAnswers.Enqueue(DraftChoice.Cancel);

        Assert.False(await shell.PrepareForUpdateAsync());

        Assert.Same(draft, shell.CurrentDraft);
        Assert.False(File.Exists(_paths.ReopenAfterUpdateFile));
        Assert.Null(_drafts.Load(draft.Id));
    }

    [Fact]
    public async Task A_discarded_draft_is_deleted_and_not_reopened()
    {
        var shell = Create();
        var draft = new Draft { Subject = "Brouillon" };
        _drafts.Save(draft);
        shell.OpenDraft(draft);
        _dialogs.DraftAnswers.Enqueue(DraftChoice.Discard);

        Assert.True(await shell.PrepareForUpdateAsync());

        Assert.False(shell.HasDraft);
        Assert.Null(_drafts.Load(draft.Id));
        Assert.Null(_state.TakeAfterUpdate()!.DraftId);
    }

    [Fact]
    public async Task An_empty_draft_is_dropped_without_asking()
    {
        var shell = Create();
        shell.OpenDraft(new Draft());

        Assert.True(await shell.ConfirmCloseDraftAsync());

        Assert.Empty(_dialogs.AskedDrafts);
        Assert.False(shell.HasDraft);
    }

    [Fact]
    public async Task A_draft_without_subject_is_named_as_such()
    {
        var shell = Create();
        shell.OpenDraft(new Draft { Body = "texte" });

        await shell.ConfirmCloseDraftAsync();

        Assert.Equal(["Draft_NoSubject"], _dialogs.AskedDrafts);
    }

    [Fact]
    public async Task A_draft_that_cannot_be_saved_is_never_lost()
    {
        // Le dossier des brouillons est un fichier : l'enregistrement échoue.
        File.WriteAllText(_paths.DraftsDirectory, "occupé");
        var shell = Create();
        var draft = new Draft { Subject = "Important" };
        shell.OpenDraft(draft);
        _dialogs.DraftAnswers.Enqueue(DraftChoice.Keep);

        Assert.False(await shell.PrepareForUpdateAsync());

        Assert.Same(draft, shell.CurrentDraft);
        Assert.Equal(["Draft_SaveFailedTitle"], _dialogs.Errors);
        Assert.False(File.Exists(_paths.ReopenAfterUpdateFile));
    }

    [Fact]
    public void A_missing_draft_does_not_prevent_restoring_the_window()
    {
        _state.SaveForUpdate(new WindowState { FolderPath = "INBOX", DraftId = Draft.NewId() });
        var shell = Create();

        Assert.True(shell.RestoreAfterUpdate());

        Assert.Equal("INBOX", shell.SelectedFolderPath);
        Assert.False(shell.HasDraft);
    }

    [Fact]
    public void Nothing_is_restored_without_a_noted_state()
    {
        var shell = Create();

        Assert.False(shell.RestoreAfterUpdate());
        Assert.Null(shell.SelectedFolderPath);
    }

    [Fact]
    public void The_most_recent_kept_draft_is_reopened_at_startup()
    {
        _drafts.Save(new Draft { Subject = "Ancien" });
        Thread.Sleep(20);
        var recent = new Draft { Subject = "Récent" };
        _drafts.Save(recent);
        var shell = Create();

        Assert.True(shell.ReopenKeptDraft());
        Assert.Equal(recent.Id, shell.CurrentDraft!.Id);
        Assert.False(shell.ReopenKeptDraft());
    }

    [Fact]
    public void Nothing_is_reopened_without_a_kept_draft()
    {
        Assert.False(Create().ReopenKeptDraft());
    }

    private ShellViewModel Create() => new(_dialogs, new KeyStrings(), _drafts, _state);
}
