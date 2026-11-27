using QuaiMail.Core.Agent;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.Tests.Agent;

public sealed class NewMailSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 8, 0, 0, TimeSpan.Zero);

    private static MessageHeader Mail(uint uid, string subject, string name, bool read = false) =>
        new(uid, subject, name, $"{uid}@example.org", Now, read, false, 1000);

    [Fact]
    public void One_unread_mail_shows_its_sender_and_subject()
    {
        var summary = NewMailSummary.For("Perso", [Mail(4, "Dîner samedi ?", "Léa")]);

        Assert.Equal(new NewMailSummary("Léa", "Dîner samedi ?"), summary);
    }

    [Fact]
    public void Several_unread_mails_show_the_count_and_the_latest_sender()
    {
        var summary = NewMailSummary.For("Perso", [Mail(6, "C", "Zoé"), Mail(5, "B", "Bob"), Mail(4, "A", "Léa")]);

        Assert.Equal(new NewMailSummary("3 nouveaux mails (Perso)", "Le plus récent : Zoé"), summary);
    }

    [Fact]
    public void Read_mails_are_not_announced()
    {
        Assert.Null(NewMailSummary.For("Perso", [Mail(4, "Déjà lu ailleurs", "Léa", read: true)]));
        Assert.Equal("Bob", NewMailSummary.For("Perso", [Mail(5, "Lu", "Léa", read: true), Mail(4, "Non lu", "Bob")])!.Title);
    }

    [Fact]
    public void Missing_subject_and_sender_name_have_a_readable_text()
    {
        var summary = NewMailSummary.For("Perso", [new MessageHeader(4, "  ", "", "lea@example.org", Now, false, false, 1)]);

        Assert.Equal(new NewMailSummary("lea@example.org", "(sans objet)"), summary);
    }

    [Fact]
    public void Texts_fit_in_the_limits_of_windows_on_one_line()
    {
        var summary = NewMailSummary.For("Perso", [Mail(4, "Ligne 1\r\nLigne 2\t" + new string('x', 400), new string('é', 100) + "😀")])!;

        Assert.True(summary.Title.Length <= NewMailSummary.MaxTitle);
        Assert.True(summary.Text.Length <= NewMailSummary.MaxText);
        Assert.EndsWith("…", summary.Text);
        Assert.StartsWith("Ligne 1 Ligne 2 xxx", summary.Text);
        Assert.DoesNotContain('\n', summary.Text);
    }

    [Fact]
    public void Clipping_never_splits_an_emoji()
    {
        var clipped = NewMailSummary.Clip("abcd😀efgh", 6);

        Assert.Equal("abcd…", clipped);
    }
}
