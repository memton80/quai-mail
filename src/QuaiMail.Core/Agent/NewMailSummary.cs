using QuaiMail.Core.Mail;

namespace QuaiMail.Core.Agent;

/// <summary>
/// Texte d'une notification de nouveaux mails, le même que celui de Quai Mail : expéditeur et objet pour un seul mail,
/// le nombre et l'expéditeur du plus récent pour plusieurs. Seuls les mails non lus comptent. Les longueurs tiennent dans
/// les limites de Windows (titre 63 caractères, texte 255) ; rien de tout cela n'est journalisé.
/// </summary>
public sealed record NewMailSummary(string Title, string Text)
{
    public const int MaxTitle = 63;

    public const int MaxText = 255;

    /// <returns><c>null</c> s'il n'y a aucun mail non lu.</returns>
    /// <param name="messages">Du plus récent au plus ancien (comme <see cref="NewMailEventArgs.Messages"/>).</param>
    public static NewMailSummary? For(string accountTitle, IReadOnlyList<MessageHeader> messages, string noSubject = "(sans objet)")
    {
        ArgumentNullException.ThrowIfNull(messages);
        var unread = messages.Where(m => !m.IsRead).ToList();
        if (unread.Count == 0)
        {
            return null;
        }

        var first = unread[0];
        return unread.Count == 1
            ? new NewMailSummary(Clip(OneLine(first.Sender), MaxTitle), Clip(string.IsNullOrWhiteSpace(first.Subject) ? noSubject : OneLine(first.Subject), MaxText))
            : new NewMailSummary(
                Clip($"{unread.Count} nouveaux mails ({OneLine(accountTitle)})", MaxTitle),
                Clip($"Le plus récent : {OneLine(first.Sender)}", MaxText));
    }

    /// <summary>Retours à la ligne et tabulations d'un objet ou d'un nom remplacés par des espaces.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Coupe proprement (jamais au milieu d'une paire de substitution), avec « … ».</summary>
    internal static string Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
    }
}
