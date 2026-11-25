using AngleSharp.Dom;
using Ganss.Xss;

namespace QuaiMail.Core.Reading;

/// <summary>HTML d'un mail nettoyé, prêt pour le gabarit de lecture.</summary>
/// <param name="Html">Fragment HTML sûr (ni script, ni cadre, ni formulaire, ni gestionnaire d'évènement).</param>
/// <param name="BlockedImages">Images distantes retirées (images, arrière-plans, CSS) : 0 si elles sont autorisées.</param>
public sealed record CleanedHtml(string Html, int BlockedImages);

/// <summary>
/// Nettoyage du HTML des mails par HtmlSanitizer (liste blanche de balises, d'attributs et de propriétés CSS) :
/// plus de script, de cadre, d'objet, de formulaire ni de gestionnaire <c>on…</c>. Liens : seulement http, https et
/// mailto. Images : jointes (<c>cid:</c>, servies par l'hôte local), intégrées (<c>data:image/…</c>), et distantes
/// seulement si l'utilisateur l'a demandé (sinon elles sont retirées et comptées : pas de pixel espion).
/// </summary>
public static class HtmlCleaner
{
    /// <summary>Hôte local qui sert la page du mail et ses images jointes (jamais un vrai serveur : <c>.example</c> est réservé).</summary>
    public const string MessageHost = "message.quaimail.example";

    public const string MessageOrigin = "https://" + MessageHost;

    /// <summary>Classe CSS des images distantes bloquées (voir <c>mail.css</c>).</summary>
    public const string BlockedClass = "quai-blocked";

    private static readonly string[] FormTags =
        ["form", "input", "button", "select", "option", "optgroup", "textarea", "datalist", "output", "fieldset", "legend", "label", "keygen"];

    private static readonly string[] SafeDataImages = ["data:image/png", "data:image/jpeg", "data:image/jpg", "data:image/gif", "data:image/webp", "data:image/bmp"];

    /// <summary>Adresse locale d'une image jointe (<c>cid:</c>).</summary>
    public static string ContentIdUrl(string contentId) => $"{MessageOrigin}/cid/{Uri.EscapeDataString(contentId)}";

    /// <param name="contentIds">Identifiants des images jointes au mail (sans chevrons) : les autres <c>cid:</c> sont retirés.</param>
    public static CleanedHtml Clean(string html, bool allowRemoteImages, IReadOnlyCollection<string> contentIds)
    {
        ArgumentNullException.ThrowIfNull(html);
        var known = new HashSet<string>(contentIds, StringComparer.OrdinalIgnoreCase);
        var blocked = 0;

        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "cid", "data" })
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        // Styles propres au mail (balise <style>, classes) : gardés, mais leur CSS passe aussi par le nettoyage.
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedAttributes.Add("class");

        // Formulaires (HtmlSanitizer les garde par défaut) : un mail n'a pas à demander de saisie, et c'est le
        // ressort de l'hameçonnage (faux formulaire de connexion).
        foreach (var tag in FormTags)
        {
            sanitizer.AllowedTags.Remove(tag);
        }

        // Attributs qui chargeraient une image distante sans passer par le filtre d'adresses, ou ouvriraient une fenêtre.
        foreach (var attribute in new[] { "srcset", "sizes", "target", "ping", "formaction", "action", "longdesc", "lowsrc", "dynsrc" })
        {
            sanitizer.AllowedAttributes.Remove(attribute);
        }

        foreach (var attribute in new[] { "src", "href", "background", "poster", "cite" })
        {
            sanitizer.UriAttributes.Add(attribute);
        }

        sanitizer.FilterUrl += (_, e) =>
        {
            var url = (e.OriginalUrl ?? string.Empty).Trim();
            var isLink = e.Tag is { LocalName: "a" or "area" } tag && string.Equals(tag.GetAttribute("href")?.Trim(), url, StringComparison.Ordinal);
            e.SanitizedUrl = isLink ? FilterLink(url) : FilterResource(url, allowRemoteImages, known, ref blocked);
        };

        sanitizer.PostProcessNode += (_, e) =>
        {
            // Image dont l'adresse a été retirée : place réservée discrète, sans rien charger.
            if (e.Node is IElement { LocalName: "img" } image && !image.HasAttribute("src"))
            {
                image.ClassList.Add(BlockedClass);
            }
        };

        var cleaned = sanitizer.Sanitize(html);
        return new CleanedHtml(cleaned, blocked);
    }

    private static string? FilterLink(string url)
    {
        if (url.StartsWith('#'))
        {
            // Ancre interne : sans effet (la navigation est de toute façon interdite).
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? uri.AbsoluteUri : null;
    }

    private static string? FilterResource(string url, bool allowRemoteImages, HashSet<string> known, ref int blocked)
    {
        if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = Uri.UnescapeDataString(url[4..]).Trim('<', '>', ' ');
            return known.Contains(id) ? ContentIdUrl(id) : null;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return SafeDataImages.Any(prefix => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ? url : null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (allowRemoteImages)
            {
                return uri.AbsoluteUri;
            }

            blocked++;
            return null;
        }

        // Adresse relative, javascript:, file:… : rien à charger.
        return null;
    }
}
