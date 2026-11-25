using QuaiMail.Core.Accounts;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.ViewModels;

public sealed class ComposerViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly AccountStore _accounts;
    private readonly DraftStore _drafts;
    private readonly FakeMailService _mail = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeFileDialogs _files = new();
    private readonly ShellViewModel _shell;
    private readonly StepTime _time = new();
    private readonly Account _account;

    public ComposerViewModelTests()
    {
        _paths = new AppPaths(_temp.Path);
        _accounts = new AccountStore(_paths);
        _drafts = new DraftStore(_paths);
        _shell = new ShellViewModel(_dialogs, new KeyStrings(), _drafts, new WindowStateStore(_paths));
        _account = new Account
        {
            Address = "alice@example.org",
            UserName = "alice@example.org",
            Imap = new MailServer { Host = "imap.example.org", Port = 993 },
            Smtp = new MailServer { Host = "smtp.example.org", Port = 465 },
        };
        _accounts.Save(_account);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_new_message_is_kept_locally_every_few_seconds_then_on_the_server()
    {
        var composer = Composer();
        Assert.True(await composer.NewAsync(null));
        Assert.True(composer.IsOpen);
        Assert.Equal(_account.Id, composer.Draft!.AccountId);

        await composer.AutoSaveAsync();
        Assert.Null(_drafts.Load(composer.Draft.Id));

        composer.To = "lea@example.org";
        composer.Subject = "Bonjour";
        composer.Body = "Texte";
        await composer.AutoSaveAsync();

        Assert.Equal("Texte", _drafts.Load(composer.Draft.Id)?.Body);
        Assert.Equal(new ServerCopy("Brouillons", 100), Assert.Single(_mail.SavedDrafts));
        Assert.Equal(100u, _drafts.Load(composer.Draft.Id)?.ServerUid);

        // Moins de 30 s plus tard : en local seulement.
        composer.Body = "Texte modifié";
        await composer.AutoSaveAsync();
        Assert.Equal("Texte modifié", _drafts.Load(composer.Draft.Id)?.Body);
        Assert.Single(_mail.SavedDrafts);

        _time.Advance(ComposerViewModel.ServerSyncInterval);
        await composer.AutoSaveAsync();
        Assert.Equal(2, _mail.SavedDrafts.Count);
        Assert.Equal(new ServerCopy("Brouillons", 100), Assert.Single(_mail.DeletedDrafts));
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task Offline_the_draft_stays_local_and_nothing_is_lost()
    {
        var composer = Composer();
        await composer.NewAsync(null);
        _mail.Failure = new MailConnectionException(MailError.HostNotFound, "imap.example.org:993", "IMAP");
        composer.Body = "Écrit dans le train";

        await composer.AutoSaveAsync();

        Assert.Equal("Écrit dans le train", _drafts.Load(composer.Draft!.Id)?.Body);
        Assert.StartsWith("Composer_SavedLocalOnly", composer.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sending_happens_only_on_request_and_cleans_up_the_draft()
    {
        var composer = Composer();
        await composer.NewAsync(null);
        composer.To = "Léa <lea@example.org>";
        composer.Subject = "Bonjour";
        composer.Body = "Texte";
        await composer.SyncServerAsync();
        var id = composer.Draft!.Id;
        Assert.Empty(_mail.Sent);

        await composer.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(_mail.Sent);
        Assert.Equal("lea@example.org", sent.To.Mailboxes.Single().Address);
        Assert.Equal("Bonjour", sent.Subject);
        Assert.Null(_drafts.Load(id));
        Assert.Contains(new ServerCopy("Brouillons", 100), _mail.DeletedDrafts);
        Assert.Null(_shell.CurrentDraft);
        Assert.False(composer.IsOpen);
        Assert.Equal("Composer_Sent", composer.SentMessage);
    }

    [Theory]
    [InlineData("", "Composer_NoRecipient")]
    [InlineData("lea@example.org, pas-une-adresse", "Composer_InvalidRecipients(pas-une-adresse)")]
    public async Task Missing_or_wrong_recipients_send_nothing(string to, string error)
    {
        var composer = Composer();
        await composer.NewAsync(null);
        composer.To = to;
        composer.Body = "Texte";

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(error, composer.ErrorMessage);
        Assert.Empty(_mail.Sent);
        Assert.True(composer.IsOpen);
    }

    [Fact]
    public async Task A_failed_send_keeps_everything()
    {
        var composer = Composer();
        await composer.NewAsync(null);
        composer.To = "lea@example.org";
        composer.Body = "Important";
        _mail.SendFailure = new MailConnectionException(MailError.Authentication, "smtp.example.org:465", "SMTP");

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal("MailError_Format(SMTP,smtp.example.org:465,MailError_Authentication)", composer.ErrorMessage);
        Assert.True(composer.IsOpen);
        Assert.Equal("Important", _drafts.Load(composer.Draft!.Id)?.Body);
    }

    [Fact]
    public async Task Closing_asks_to_keep_or_delete_the_draft_everywhere()
    {
        var composer = Composer();
        await composer.NewAsync(null);
        composer.Body = "À garder";
        await composer.SyncServerAsync();
        var kept = composer.Draft!.Id;

        _dialogs.DraftAnswers.Enqueue(QuaiMail.Core.Services.DraftChoice.Cancel);
        await composer.CloseCommand.ExecuteAsync(null);
        Assert.True(composer.IsOpen);

        _dialogs.DraftAnswers.Enqueue(QuaiMail.Core.Services.DraftChoice.Keep);
        await composer.CloseCommand.ExecuteAsync(null);
        Assert.False(composer.IsOpen);
        Assert.Equal("À garder", _drafts.Load(kept)?.Body);

        await composer.NewAsync(null);
        composer.Body = "À jeter";
        await composer.SyncServerAsync();
        var discarded = composer.Draft!.Id;
        var copy = new ServerCopy("Brouillons", composer.Draft.ServerUid);
        _dialogs.DraftAnswers.Enqueue(QuaiMail.Core.Services.DraftChoice.Discard);
        await composer.CloseCommand.ExecuteAsync(null);

        Assert.False(composer.IsOpen);
        Assert.Null(_drafts.Load(discarded));
        Assert.Contains(copy, _mail.DeletedDrafts);
    }

    [Fact]
    public async Task Reply_and_forward_open_prepared_drafts()
    {
        var builder = new MimeKit.BodyBuilder { TextBody = "Voici le devis." };
        builder.Attachments.Add("devis.pdf", "%PDF"u8.ToArray());
        var original = MessageContent.FromMime(4, new MimeKit.MimeMessage([new MimeKit.MailboxAddress("Léa", "lea@example.org")], [new MimeKit.MailboxAddress("Alice", "alice@example.org")], "Devis", builder.ToMessageBody()));
        var composer = Composer();

        await composer.ReplyAsync(original, _account, all: false);
        Assert.Equal("Léa <lea@example.org>", composer.To);
        Assert.Equal("Re: Devis", composer.Subject);
        Assert.Contains("> Voici le devis.", composer.Body, StringComparison.Ordinal);
        Assert.Empty(composer.Attachments);

        _dialogs.DraftAnswers.Enqueue(QuaiMail.Core.Services.DraftChoice.Discard);
        await composer.ForwardAsync(original, _account);

        Assert.Equal("TR: Devis", composer.Subject);
        var attachment = Assert.Single(composer.Attachments);
        Assert.Equal("devis.pdf", attachment.FileName);
        Assert.Equal("%PDF", File.ReadAllText(_drafts.AttachmentPath(composer.Draft!, attachment)!));
    }

    [Fact]
    public async Task Attached_files_are_copied_and_can_be_removed()
    {
        var composer = Composer();
        await composer.NewAsync(null);
        var file = _temp.File("plan.txt");
        File.WriteAllText(file, "plan");
        _files.ToAttach.Add(file);

        await composer.AttachCommand.ExecuteAsync(null);

        var attachment = Assert.Single(composer.Attachments);
        Assert.Equal("plan.txt", attachment.FileName);
        Assert.Single(_drafts.Load(composer.Draft!.Id)!.Attachments);

        composer.RemoveAttachment(attachment);
        Assert.Empty(composer.Attachments);
        Assert.Empty(composer.Draft.Attachments);
    }

    [Fact]
    public async Task A_draft_restored_after_an_update_opens_in_the_composer()
    {
        var draft = new Draft { AccountId = _account.Id, To = "lea@example.org", Subject = "Repris", Body = "Suite" };
        _drafts.Save(draft);
        new WindowStateStore(_paths).SaveForUpdate(new QuaiMail.Core.Models.WindowState { DraftId = draft.Id });
        var composer = Composer();

        Assert.True(_shell.RestoreAfterUpdate());

        Assert.True(composer.IsOpen);
        Assert.Equal("Repris", composer.Subject);
        Assert.Equal("Suite", composer.Body);
    }

    private ComposerViewModel Composer() => new(_accounts, _drafts, _mail, _shell, _dialogs, _files, new KeyStrings(), _time);

    private sealed class StepTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
