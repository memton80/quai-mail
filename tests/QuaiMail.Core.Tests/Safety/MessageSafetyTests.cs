using MimeKit;
using QuaiMail.Core.Safety;

namespace QuaiMail.Core.Tests.Safety;

public sealed class MessageSafetyTests
{
    private static readonly string[] Known = ["example.org", "paypal.com", "gmail.com"];

    [Theory]
    [InlineData("mx.example.org; dkim=pass header.d=example.org; spf=pass smtp.mailfrom=example.org; dmarc=pass header.from=example.org", false)]
    [InlineData("mx.example.org; dkim=none; spf=fail smtp.mailfrom=example.org; dmarc=fail (p=REJECT) header.from=example.org", true)]
    [InlineData("mx.example.org; spf=fail smtp.mailfrom=example.org; dkim=pass header.d=example.org; dmarc=pass header.from=example.org", false)]
    [InlineData("mx.example.org; spf=fail smtp.mailfrom=example.org; dkim=none", true)]
    [InlineData("mx.example.org; spf=fail smtp.mailfrom=example.org; dkim=pass header.d=example.org", false)]
    [InlineData("mx.example.org; spf=softfail smtp.mailfrom=example.org", false)]
    [InlineData("spf=fail (sender IP is 192.0.2.1) smtp.mailfrom=example.org; dkim=none (message not signed) header.d=none;dmarc=fail action=quarantine header.from=example.org;", true)]
    public void The_receiving_server_verdict_is_read(string results, bool failed)
    {
        var message = Message();
        message.Headers.Add("Authentication-Results", results);

        Assert.Equal(failed, MessageSafety.AuthenticationFailed(message));
    }

    [Fact]
    public void Only_the_topmost_results_count_and_a_missing_header_proves_nothing()
    {
        Assert.False(MessageSafety.AuthenticationFailed(Message()));

        // Le serveur de réception ajoute son verdict en tête ; plus bas, ce que l'expéditeur a pu écrire lui-même.
        var message = Message();
        message.Headers.Add("Authentication-Results", "mx.example.org; dmarc=pass header.from=example.org");
        message.Headers.Add("Authentication-Results", "faux.example; dmarc=fail header.from=example.org");
        Assert.False(MessageSafety.AuthenticationFailed(message));

        var forged = Message();
        forged.Headers.Add("Authentication-Results", "mx.example.org; dmarc=fail header.from=example.org");
        forged.Headers.Add("Authentication-Results", "faux.example; dmarc=pass header.from=example.org");
        Assert.True(MessageSafety.AuthenticationFailed(forged));
    }

    [Fact]
    public void A_clean_unknown_mail_has_no_warning()
    {
        var links = MessageSafety.Links("""<p><a href="https://www.example.org/compte">www.example.org</a> <a href="https://example.org/x">Lire la suite</a></p>""");

        Assert.Empty(MessageSafety.Analyze("Bob", "bob@example.org", ["bob@example.org"], false, links, Known));
    }

    [Fact]
    public void Each_clue_is_reported_with_what_to_check()
    {
        var links = MessageSafety.Links("""
            <a href="https://evil.example/connexion">https://www.paypal.com/mon-compte</a>
            <a href="http://192.0.2.7/login">Se connecter</a>
            <a href="https://evil.example/facture.pdf">facture.pdf</a>
            """);

        var warnings = MessageSafety.Analyze(
            "service@paypal.com", "alerte@paypa1.com", ["recouvrement@autre.example"], true, links, Known);

        Assert.Equal(
            [
                new SafetyWarning(SafetyWarningKind.AuthenticationFailed, "paypa1.com"),
                new SafetyWarning(SafetyWarningKind.NameShowsOtherAddress, "service@paypal.com", "alerte@paypa1.com"),
                new SafetyWarning(SafetyWarningKind.ReplyToElsewhere, "recouvrement@autre.example"),
                new SafetyWarning(SafetyWarningKind.LookalikeDomain, "paypa1.com", "paypal.com"),
                new SafetyWarning(SafetyWarningKind.DeceptiveLink, "www.paypal.com", "evil.example"),
                new SafetyWarning(SafetyWarningKind.NumericLink, "192.0.2.7"),
            ],
            warnings);
        Assert.Equal(["evil.example"], MessageSafety.DeceptiveHosts(links));
    }

    [Theory]
    [InlineData("examp1e.org", "example.org")]
    [InlineData("exarnple.org", "example.org")]
    [InlineData("gmai.com", "gmail.com")]
    [InlineData("paypal.co", "paypal.com")]
    public void Lookalike_domains_are_caught(string domain, string original)
    {
        var warning = Assert.Single(MessageSafety.Analyze(string.Empty, "x@" + domain, [], false, [], Known));

        Assert.Equal(new SafetyWarning(SafetyWarningKind.LookalikeDomain, domain, original), warning);
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("news.example.org")]
    [InlineData("exemple.fr")]
    [InlineData("banque.example")]
    public void Real_or_unrelated_domains_are_not_lookalikes(string domain) =>
        Assert.Empty(MessageSafety.Analyze(string.Empty, "x@" + domain, [], false, [], Known));

    [Fact]
    public void An_international_domain_is_shown_in_both_forms()
    {
        var warning = Assert.Single(MessageSafety.Analyze(string.Empty, "x@xn--pypal-4ve.com", [], false, [], Known));

        Assert.Equal(new SafetyWarning(SafetyWarningKind.InternationalDomain, "pаypal.com", "xn--pypal-4ve.com"), warning);
    }

    [Fact]
    public void Replies_to_a_subdomain_of_the_sender_are_fine() =>
        Assert.Empty(MessageSafety.Analyze("Banque", "info@banque.example", ["conseil@mail.banque.example"], false, [], Known));

    [Fact]
    public void Plain_text_mails_have_no_deceptive_link() =>
        Assert.Empty(MessageSafety.Links(null));

    private static MimeMessage Message() =>
        new([new MailboxAddress("Bob", "bob@example.org")], [new MailboxAddress("Alice", "alice@example.org")], "Objet", new TextPart("plain") { Text = "Bonjour" });
}
