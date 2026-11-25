namespace QuaiMail.Core.Reading;

/// <summary>
/// Page de lecture d'un mail : le gabarit <c>Assets/Mail/reader.html</c> (aucun style, aucun script, politique CSP
/// stricte) dans lequel le corps nettoyé remplace le marqueur. Si l'utilisateur a demandé les images distantes, la
/// politique les autorise, et rien d'autre.
/// </summary>
public static class ReaderPage
{
    public const string BodyMarker = "<!-- QUAI_MAIL_BODY -->";

    /// <summary>Source des images dans la politique CSP du gabarit (stricte : rien de distant).</summary>
    public const string StrictImageSource = "img-src data: https://reader.quaimail.example https://message.quaimail.example;";

    /// <summary>Même politique, images distantes en plus (après « Afficher les images »).</summary>
    public const string RemoteImageSource = "img-src data: https://reader.quaimail.example https://message.quaimail.example https: http:;";

    /// <exception cref="InvalidOperationException">Gabarit sans marqueur ou sans la politique attendue : rien n'est affiché.</exception>
    public static string Build(string template, string cleanedBody, bool allowRemoteImages)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Contains(BodyMarker, StringComparison.Ordinal) || !template.Contains(StrictImageSource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Gabarit de lecture inattendu : marqueur ou politique CSP absents.");
        }

        var page = allowRemoteImages ? template.Replace(StrictImageSource, RemoteImageSource, StringComparison.Ordinal) : template;
        return page.Replace(BodyMarker, cleanedBody, StringComparison.Ordinal);
    }
}
