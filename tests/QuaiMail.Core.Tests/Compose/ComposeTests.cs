using MimeKit;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Compose;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Compose;

public sealed class ComposeTests
{
    private static readonly Account Alice = new()
    {
        Address = "alice@example.org",
        DisplayName = "Alice Martin",
        UserName = "alice@example.org",
        Imap = new MailServer { Host = "imap.example.org", Port = 993 },
        Smtp = new MailServer { Host = "smtp.example.org", Port = 465 },
    };

    [Theory]
    [InlineData("Devis", "Re: Devis")]
    [InlineData("Re: Devis", "Re: Devis")]
    [InlineData("RE: Devis", "RE: Devis")]
    [InlineData("  ", "Re: ")]
    public void Reply_subjects_do_not_pile_up(string subject, string expected) => Assert.Equal(expected, ReplyBuilder.ReplySubject(subject));

    [Theory]
    [InlineData("Devis", "TR: Devis")]
    [InlineData("TR: Devis", "TR: Devis")]
    [InlineData("Fwd: Devis", "Fwd: Devis")]
    public void Forward_subjects_do_not_pile_up(string subject, string expected) => Assert.Equal(expected, ReplyBuilder.ForwardSubject(subject));

    [Fact]
    public void A_reply_goes_to_the_sender_quotes_the_text_and_keeps_the_thread()
    {
        var original = Original(replyTo: null);

        var draft = ReplyBuilder.Reply(original, Alice, all: false);

        Assert.Equal(DraftKind.Reply, draft.Kind);
        Assert.Equal(Alice.Id, draft.AccountId);
        Assert.Equal("Léa Dubois <lea@example.org>", draft.To);
        Assert.Equal(string.Empty, draft.Cc);
        Assert.Equal("Re: Réunion de jeudi", draft.Subject);
        Assert.Equal("origine@example.org", draft.InReplyTo);
        Assert.Equal(["fil@example.org", "origine@example.org"], draft.References);
        Assert.StartsWith("\n\nLe ", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Léa Dubois <lea@example.org> a écrit :\n> Bonjour à tous,\n> \n>> déjà cité\n> À jeudi.", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reply_honours_the_reply_to_address()
    {
        var draft = ReplyBuilder.Reply(Original(replyTo: "liste@example.org"), Alice, all: false);

        Assert.Equal("liste@example.org", draft.To);
    }

    [Fact]
    public void A_reply_to_all_adds_the_others_but_never_oneself()
    {
        var draft = ReplyBuilder.Reply(Original(replyTo: null), Alice, all: true);

        Assert.Equal(DraftKind.ReplyAll, draft.Kind);
        Assert.Equal("Léa Dubois <lea@example.org>, Bob <bob@example.org>", draft.To);
        Assert.Equal("\"Dupont, Jean\" <jean@example.org>", draft.Cc);
        Assert.DoesNotContain("alice@example.org", draft.To + draft.Cc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_forward_carries_the_original_headers_and_text()
    {
        var draft = ReplyBuilder.Forward(Original(replyTo: null), Alice);

        Assert.Equal(DraftKind.Forward, draft.Kind);
        Assert.Equal(string.Empty, draft.To);
        Assert.Equal("TR: Réunion de jeudi", draft.Subject);
        Assert.Contains("---------- Message transféré ----------\nDe : Léa Dubois <lea@example.org>\n", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Objet : Réunion de jeudi\n", draft.Body, StringComparison.Ordinal);
        Assert.Contains("Bonjour à tous,", draft.Body, StringComparison.Ordinal);
        Assert.Null(draft.InReplyTo);
    }

    [Fact]
    public void An_html_only_mail_is_quoted_as_text_without_anything_active()
    {
        var html = new MimeMessage([new MailboxAddress("Bob", "bob@example.org")], [], "Promo",
            new TextPart("html") { Text = "<p>Bonjour <b>Alice</b>,</p><script>alert(1)</script><div>Voir <a href=\"https://example.org/offre\">l'offre</a></div><ul><li>un</li><li>deux</li></ul>" });

        var text = ReplyBuilder.PlainText(MessageContent.FromMime(1, html));

        Assert.Equal("Bonjour Alice,\nVoir l'offre <https://example.org/offre>\nun\ndeux", text);
    }

    [Fact]
    public void Recipients_are_read_one_by_one_and_invalid_ones_reported()
    {
        var valid = Recipients.Parse("Léa <lea@example.org>; bob@example.org, \"Dupont, Jean\" <jean@example.org>, pas-une-adresse, x@localhost", out var invalid);

        Assert.Equal(["lea@example.org", "bob@example.org", "jean@example.org"], valid.Select(m => m.Address));
        Assert.Equal("Dupont, Jean", valid[2].Name);
        Assert.Equal(["pas-une-adresse", "x@localhost"], invalid);
        Assert.Empty(Recipients.Parse("  ", out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void The_mime_message_has_the_thread_headers_hidden_copies_and_attachments()
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));
        var draft = new Draft
        {
            AccountId = Alice.Id,
            To = "Léa <lea@example.org>",
            Bcc = "cache@example.org",
            Subject = " Devis ",
            Body = "Bonjour,\nvoici le devis.",
            InReplyTo = "origine@example.org",
            References = ["fil@example.org", "origine@example.org"],
        };
        store.AddAttachmentAsync(draft, "devis.pdf", (s, ct) => s.WriteAsync("%PDF"u8.ToArray(), ct).AsTask()).GetAwaiter().GetResult();

        var message = MessageComposer.Build(Alice, draft, store, forSending: true);

        Assert.Equal("\"Alice Martin\" <alice@example.org>", message.From.ToString());
        Assert.Equal("lea@example.org", message.To.Mailboxes.Single().Address);
        Assert.Equal("cache@example.org", message.Bcc.Mailboxes.Single().Address);
        Assert.Equal("Devis", message.Subject);
        Assert.Equal("origine@example.org", message.InReplyTo);
        Assert.Equal(["fil@example.org", "origine@example.org"], message.References);
        Assert.EndsWith("@example.org", message.MessageId, StringComparison.Ordinal);
        Assert.Equal("Bonjour,\nvoici le devis.", message.TextBody.Replace("\r\n", "\n", StringComparison.Ordinal));
        var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("devis.pdf", attachment.FileName);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("pas une adresse", "pas une adresse")]
    public void Nothing_is_built_for_sending_without_valid_recipients(string to, string invalid)
    {
        using var temp = new TempDirectory();
        var draft = new Draft { AccountId = Alice.Id, To = to, Subject = "x", Body = "y" };

        var error = Assert.Throws<InvalidRecipientsException>(() => MessageComposer.Build(Alice, draft, new DraftStore(temp.Path), forSending: true));

        Assert.Equal(invalid.Length == 0 ? [] : [invalid], error.Invalid);

        // Un brouillon, lui, s'enregistre même incomplet.
        Assert.NotNull(MessageComposer.Build(Alice, draft, new DraftStore(temp.Path), forSending: false));
    }

    [Fact]
    public void Draft_attachments_are_copies_that_leave_with_the_draft()
    {
        using var temp = new TempDirectory();
        var store = new DraftStore(temp.File("drafts"));
        var source = temp.File("photo.jpg");
        File.WriteAllText(source, "image");
        var draft = new Draft { Subject = "Photo" };

        var attachment = store.AddAttachmentAsync(draft, "photo.jpg", async (s, ct) =>
        {
            await using var file = File.OpenRead(source);
            await file.CopyToAsync(s, ct);
        }).GetAwaiter().GetResult();
        File.Delete(source);
        store.Save(draft);

        var reloaded = store.Load(draft.Id)!;
        Assert.Equal(attachment, Assert.Single(reloaded.Attachments));
        Assert.Equal("image", File.ReadAllText(store.AttachmentPath(reloaded, attachment)!));
        Assert.False(draft.IsEmpty);
        Assert.False(new Draft { Attachments = [attachment] }.IsEmpty);

        store.Delete(draft.Id);
        Assert.Empty(Directory.GetFileSystemEntries(store.Directory));
    }

    private static MessageContent Original(string? replyTo)
    {
        var message = new MimeMessage(
            [new MailboxAddress("Léa Dubois", "lea@example.org")],
            [new MailboxAddress("Alice", "alice@example.org"), new MailboxAddress("Bob", "bob@example.org")],
            "Réunion de jeudi",
            new TextPart("plain") { Text = "Bonjour à tous,\r\n\r\n> déjà cité\r\nÀ jeudi." })
        {
            MessageId = "origine@example.org",
            Date = new DateTimeOffset(2026, 11, 20, 9, 15, 0, TimeSpan.FromHours(1)),
        };
        message.Cc.Add(new MailboxAddress("Dupont, Jean", "jean@example.org"));
        message.Cc.Add(new MailboxAddress("Alice M.", "ALICE@example.org"));
        message.References.Add("fil@example.org");
        if (replyTo is not null)
        {
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        }

        return MessageContent.FromMime(5, message);
    }
}
