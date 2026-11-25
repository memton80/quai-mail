using QuaiMail.Core.Compose;

namespace QuaiMail.Core.Tests.Compose;

public sealed class MailtoLinkTests
{
    [Fact]
    public void A_full_link_fills_the_draft()
    {
        Assert.True(MailtoLink.TryParse(
            "mailto:lea@example.org,bob@example.org?cc=jean@example.org&bcc=cache@example.org&subject=R%C3%A9union%20de%20jeudi&body=Bonjour%2C%0D%0A%C3%A0%20jeudi%20%2B%20merci",
            out var link));

        Assert.Equal("lea@example.org,bob@example.org", link.To);
        Assert.Equal("jean@example.org", link.Cc);
        Assert.Equal("cache@example.org", link.Bcc);
        Assert.Equal("Réunion de jeudi", link.Subject);
        Assert.Equal("Bonjour,\nà jeudi + merci", link.Body);
        var draft = link.ToDraft("compte");
        Assert.Equal(("compte", "Réunion de jeudi"), (draft.AccountId, draft.Subject));
    }

    [Theory]
    [InlineData("mailto:lea@example.org", "lea@example.org", "")]
    [InlineData("\"MAILTO:lea@example.org?Subject=Hello\"", "lea@example.org", "Hello")]
    [InlineData("mailto:?to=lea@example.org&to=bob@example.org", "lea@example.org, bob@example.org", "")]
    [InlineData("mailto:", "", "")]
    [InlineData("mailto:a+b@example.org?subject=a+b", "a+b@example.org", "a+b")]
    public void Simple_links_are_read(string text, string to, string subject)
    {
        Assert.True(MailtoLink.TryParse(text, out var link));
        Assert.Equal(to, link.To);
        Assert.Equal(subject, link.Subject);
    }

    [Fact]
    public void Attachments_and_other_headers_are_ignored_and_the_subject_stays_on_one_line()
    {
        Assert.True(MailtoLink.TryParse("mailto:lea@example.org?attach=C:%5CWindows%5Cwin.ini&in-reply-to=x&subject=Un%0ADeux&x-mailer=y", out var link));

        Assert.Equal("Un Deux", link.Subject);
        Assert.Equal(string.Empty, link.Body);
    }

    [Theory]
    [InlineData("https://example.org")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_things_are_not_links(string? text) => Assert.False(MailtoLink.TryParse(text, out _));

    [Fact]
    public void A_link_is_found_in_a_command_line()
    {
        Assert.True(MailtoLink.TryFind(["C:\\Programs\\QuaiMail.exe", "\"mailto:lea@example.org?subject=Oui\""], out var link));
        Assert.Equal("Oui", link.Subject);
        Assert.False(MailtoLink.TryFind(["QuaiMail.exe", "--after-update"], out _));
    }
}
