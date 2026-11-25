using QuaiMail.Core.Drafts;

namespace QuaiMail.Core.Compose;

/// <summary>
/// Lien <c>mailto:</c> (RFC 6068), reçu quand Quai Mail est choisi pour les liens mail : destinataires, copie, copie
/// cachée, objet, texte. Tout autre champ est ignoré (pas de pièce jointe, pas d'en-tête imposé par le lien). Le lien
/// ne fait qu'ouvrir un brouillon : rien n'est envoyé sans le bouton Envoyer.
/// </summary>
public sealed record MailtoLink(string To, string Cc, string Bcc, string Subject, string Body)
{
    private const int MaxSubject = 500;
    private const int MaxBody = 100_000;

    /// <summary>Cherche un lien mailto dans une ligne de commande ou un argument (guillemets tolérés).</summary>
    public static bool TryFind(IEnumerable<string> arguments, out MailtoLink link)
    {
        foreach (var argument in arguments)
        {
            var start = argument.IndexOf("mailto:", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && TryParse(argument[start..].Trim().TrimEnd('"'), out link))
            {
                return true;
            }
        }

        link = new MailtoLink(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        return false;
    }

    public static bool TryParse(string? text, out MailtoLink link)
    {
        link = new MailtoLink(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        var value = (text ?? string.Empty).Trim().Trim('"');
        if (!value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[7..];
        var question = rest.IndexOf('?');
        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();
        string subject = string.Empty, body = string.Empty;
        if ((question < 0 ? rest : rest[..question]) is { Length: > 0 } addresses)
        {
            to.Add(Decode(addresses));
        }

        if (question >= 0)
        {
            foreach (var pair in rest[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var key = Decode(equals < 0 ? pair : pair[..equals]).ToLowerInvariant();
                var field = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);
                switch (key)
                {
                    case "to":
                        to.Add(field);
                        break;
                    case "cc":
                        cc.Add(field);
                        break;
                    case "bcc":
                        bcc.Add(field);
                        break;
                    case "subject":
                        subject = field.Length > MaxSubject ? field[..MaxSubject] : field;
                        break;
                    case "body":
                        body = field.Length > MaxBody ? field[..MaxBody] : field;
                        break;
                }
            }
        }

        static string Join(List<string> parts) => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        link = new MailtoLink(Join(to), Join(cc), Join(bcc), subject.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal), body.Replace("\r\n", "\n", StringComparison.Ordinal));
        return true;
    }

    public Draft ToDraft(string? accountId) => new()
    {
        AccountId = accountId,
        To = To,
        Cc = Cc,
        Bcc = Bcc,
        Subject = Subject,
        Body = Body,
    };

    private static string Decode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
