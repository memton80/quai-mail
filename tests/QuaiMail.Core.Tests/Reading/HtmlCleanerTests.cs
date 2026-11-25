using QuaiMail.Core.Reading;

namespace QuaiMail.Core.Tests.Reading;

/// <summary>Mails piégés : rien d'actif, rien de distant (sauf demande), rien qui sorte de la page.</summary>
public sealed class HtmlCleanerTests
{
    private static CleanedHtml Clean(string html, bool remote = false, params string[] cids) => HtmlCleaner.Clean(html, remote, cids);

    [Theory]
    [InlineData("<script>alert(1)</script><p>Bonjour</p>")]
    [InlineData("<p>Bonjour</p><SCRIPT SRC=https://evil.example/x.js></SCRIPT>")]
    [InlineData("<p onclick=\"alert(1)\" onmouseover='steal()'>Bonjour</p>")]
    [InlineData("<p>Bonjour</p><img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">Bonjour</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">Bonjour</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">Bonjour</a>")]
    [InlineData("<svg onload=alert(1)><p>Bonjour</p></svg>")]
    [InlineData("<p>Bonjour</p><body onload=alert(1)>")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">Bonjour</div>")]
    [InlineData("<div style=\"width: expression(alert(1))\">Bonjour</div>")]
    public void Nothing_active_survives(string html)
    {
        var cleaned = Clean(html).Html;

        Assert.Contains("Bonjour", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("script", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expression", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<iframe src=\"https://evil.example/\"></iframe>", "iframe")]
    [InlineData("<frameset><frame src=\"https://evil.example/\"></frameset>", "frame")]
    [InlineData("<object data=\"https://evil.example/x.swf\"></object>", "object")]
    [InlineData("<embed src=\"https://evil.example/x.swf\">", "embed")]
    [InlineData("<form action=\"https://evil.example/vol\"><input name=password type=password><button>Valider</button></form>", "form")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example/\">", "refresh")]
    [InlineData("<base href=\"https://evil.example/\">", "base")]
    [InlineData("<link rel=stylesheet href=\"https://evil.example/x.css\">", "link")]
    [InlineData("<audio src=\"https://evil.example/a.mp3\" autoplay></audio>", "audio")]
    [InlineData("<video poster=\"https://evil.example/p.jpg\"></video>", "video")]
    public void Frames_objects_forms_and_redirections_are_removed(string html, string marker)
    {
        var cleaned = Clean("<p>Texte</p>" + html).Html;

        Assert.Contains("Texte", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("<" + marker, cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<img src=\"https://tracker.example/pixel.gif\" width=1 height=1>")]
    [InlineData("<img src=\"http://tracker.example/pixel.gif\">")]
    [InlineData("<img srcset=\"https://tracker.example/pixel.gif 1x\">")]
    [InlineData("<table background=\"https://tracker.example/fond.png\"><tr><td>x</td></tr></table>")]
    [InlineData("<div style=\"background-image: url('https://tracker.example/fond.png')\">x</div>")]
    [InlineData("<style>body { background: url(https://tracker.example/fond.png) }</style>")]
    [InlineData("<style>@import url(https://tracker.example/x.css);</style>")]
    [InlineData("<picture><source srcset=\"https://tracker.example/a.webp\"><img src=\"https://tracker.example/a.jpg\"></picture>")]
    public void Remote_images_and_tracking_pixels_are_blocked_by_default(string html)
    {
        var cleaned = Clean("<p>Texte</p>" + html);

        Assert.DoesNotContain("tracker.example", cleaned.Html, StringComparison.Ordinal);
        Assert.Contains("Texte", cleaned.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Blocked_images_are_counted_and_keep_a_discreet_place()
    {
        var cleaned = Clean("<img src=\"https://tracker.example/pixel.gif\" alt=\"Logo\"><div style=\"background:url(https://cdn.example/b.png)\">x</div>");

        Assert.Equal(2, cleaned.BlockedImages);
        Assert.Contains($"class=\"{HtmlCleaner.BlockedClass}\"", cleaned.Html, StringComparison.Ordinal);
        Assert.Contains("alt=\"Logo\"", cleaned.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_images_are_shown_only_on_request()
    {
        var cleaned = Clean("<img src=\"https://cdn.example/photo.jpg\">", remote: true);

        Assert.Contains("src=\"https://cdn.example/photo.jpg\"", cleaned.Html, StringComparison.Ordinal);
        Assert.Equal(0, cleaned.BlockedImages);
    }

    [Fact]
    public void Attached_images_are_served_by_the_local_host_and_unknown_ones_dropped()
    {
        var cleaned = Clean("<img src=\"cid:logo@example.org\"><img src=\"cid:inconnu@example.org\">", false, "logo@example.org");

        Assert.Contains($"src=\"{HtmlCleaner.MessageOrigin}/cid/logo%40example.org\"", cleaned.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("inconnu", cleaned.Html, StringComparison.Ordinal);
        Assert.Equal(0, cleaned.BlockedImages);
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", true)]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=", false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    public void Embedded_images_are_kept_only_for_plain_image_types(string url, bool kept)
    {
        var cleaned = Clean($"<img src=\"{url}\">").Html;

        Assert.Equal(kept, cleaned.Contains(url, StringComparison.Ordinal));
    }

    [Fact]
    public void Links_keep_their_real_target_but_lose_every_trick()
    {
        // Lien trompeur : le texte montre la banque, l'adresse mène ailleurs. L'adresse réelle est gardée telle quelle :
        // c'est elle que la confirmation montre avant d'ouvrir le navigateur.
        var cleaned = Clean("<a href=\"https://evil.example/connexion\" target=\"_blank\" ping=\"https://tracker.example/\">https://www.banque.example</a>"
            + "<a href=\"mailto:alice@example.org\">écrire</a><a href=\"file:///C:/Windows/system32/calc.exe\">calc</a><a href=\"ftp://example.org/\">ftp</a>").Html;

        Assert.Contains("href=\"https://evil.example/connexion\"", cleaned, StringComparison.Ordinal);
        Assert.Contains("href=\"mailto:alice@example.org\"", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("target", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("ping", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("ftp:", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void A_background_url_on_a_link_is_still_an_image()
    {
        var cleaned = Clean("<a href=\"https://example.org/\" style=\"background:url(https://tracker.example/p.gif)\">lien</a>");

        Assert.Contains("href=\"https://example.org/\"", cleaned.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.example", cleaned.Html, StringComparison.Ordinal);
        Assert.Equal(1, cleaned.BlockedImages);
    }

    [Fact]
    public void Ordinary_formatting_is_kept()
    {
        var cleaned = Clean("<h1>Titre</h1><p style=\"color: red\">Texte <b>gras</b> <i>italique</i></p><blockquote>citation</blockquote><table><tr><td>case</td></tr></table>").Html;

        Assert.Contains("<h1>Titre</h1>", cleaned, StringComparison.Ordinal);
        Assert.Contains("style=\"color:", cleaned, StringComparison.Ordinal);
        Assert.Contains("<b>gras</b>", cleaned, StringComparison.Ordinal);
        Assert.Contains("<blockquote>", cleaned, StringComparison.Ordinal);
        Assert.Contains("<td>case</td>", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_uses_the_template_and_its_strict_policy()
    {
        var template = File.ReadAllText(TemplatePath());

        var page = ReaderPage.Build(template, "<p>Corps</p>", allowRemoteImages: false);
        var withImages = ReaderPage.Build(template, "<p>Corps</p>", allowRemoteImages: true);

        Assert.Contains("<p>Corps</p>", page, StringComparison.Ordinal);
        Assert.DoesNotContain(ReaderPage.BodyMarker, page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", page, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", page, StringComparison.Ordinal);
        Assert.Contains(ReaderPage.StrictImageSource, page, StringComparison.Ordinal);
        Assert.DoesNotContain("https:;", page, StringComparison.Ordinal);
        Assert.Contains(ReaderPage.RemoteImageSource, withImages, StringComparison.Ordinal);
        Assert.Contains("href=\"https://reader.quaimail.example/mail.css\"", page, StringComparison.Ordinal);

        // Règle du projet : ni CSS ni script dans le gabarit.
        Assert.DoesNotContain("<style", template, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\sstyle\s*=", template);
        Assert.DoesNotContain("<script", template, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unexpected_template_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ReaderPage.Build("<html><body><!-- QUAI_MAIL_BODY --></body></html>", "x", false));
    }

    internal static string TemplatePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuaiMail.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "QuaiMail.App", "Assets", "Mail", "reader.html");
    }
}
