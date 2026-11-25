using System.Globalization;
using MimeKit;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Reading;

namespace QuaiMail.Core.Compose;

/// <summary>Brouillons préparés à partir d'un mail reçu : réponse, réponse à tous, transfert.</summary>
public static class ReplyBuilder
{
    /// <summary>« Re: objet » (sans empiler les « Re: »).</summary>
    public static string ReplySubject(string subject) => Prefixed("Re: ", subject, ["re:", "ré:", "re :", "ré :", "aw:", "sv:"]);

    /// <summary>« TR: objet » (préfixe français des transferts).</summary>
    public static string ForwardSubject(string subject) => Prefixed("TR: ", subject, ["tr:", "tr :", "fw:", "fwd:"]);

    /// <summary>Réponse à l'expéditeur (ou à l'adresse de réponse qu'il demande) ; à tous : plus les autres destinataires, sauf soi.</summary>
    public static Draft Reply(MessageContent original, Account account, bool all)
    {
        var self = account.Address;
        var to = original.ReplyToAddresses.Count > 0
            ? original.ReplyToAddresses.ToList()
            : [new MailboxAddress(original.FromName, original.FromAddress)];
        var cc = new List<MailboxAddress>();
        if (all)
        {
            to.AddRange(original.ToAddresses);
            cc.AddRange(original.CcAddresses);
        }

        return new Draft
        {
            AccountId = account.Id,
            Kind = all ? DraftKind.ReplyAll : DraftKind.Reply,
            To = Recipients.Format(Distinct(to, self, [])),
            Cc = Recipients.Format(Distinct(cc, self, to)),
            Subject = ReplySubject(original.Subject),
            Body = "\n\n" + Quote(original),
            InReplyTo = original.MessageId,
            References = Thread(original),
        };
    }

    /// <summary>Transfert : objet « TR: », en-têtes et texte du mail d'origine (ses pièces jointes sont copiées à part).</summary>
    public static Draft Forward(MessageContent original, Account account) => new()
    {
        AccountId = account.Id,
        Kind = DraftKind.Forward,
        Subject = ForwardSubject(original.Subject),
        Body = "\n\n---------- Message transféré ----------\n"
            + $"De : {original.From}\n"
            + $"Date : {FormatDate(original.Date)}\n"
            + $"Objet : {original.Subject}\n"
            + $"À : {original.To}\n"
            + (string.IsNullOrEmpty(original.Cc) ? string.Empty : $"Cc : {original.Cc}\n")
            + "\n" + PlainText(original),
        References = Thread(original),
    };

    /// <summary>Texte du mail : sa version texte, sinon le texte de sa version HTML.</summary>
    public static string PlainText(MessageContent original) =>
        !string.IsNullOrWhiteSpace(original.Text) ? original.Text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()
        : !string.IsNullOrWhiteSpace(original.Html) ? HtmlText.ToPlainText(original.Html)
        : string.Empty;

    /// <summary>« Le …, … a écrit : » puis chaque ligne précédée de « &gt; ».</summary>
    public static string Quote(MessageContent original)
    {
        var lines = PlainText(original).Split('\n').Select(line => line.StartsWith('>') ? ">" + line : "> " + line);
        return $"Le {FormatDate(original.Date)}, {original.From} a écrit :\n" + string.Join('\n', lines);
    }

    private static string FormatDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("dddd d MMMM yyyy 'à' HH:mm", CultureInfo.GetCultureInfo("fr-FR"));

    private static List<string> Thread(MessageContent original)
    {
        var references = original.References.ToList();
        if (original.MessageId is { Length: > 0 } id && !references.Contains(id))
        {
            references.Add(id);
        }

        return references;
    }

    private static IEnumerable<MailboxAddress> Distinct(IEnumerable<MailboxAddress> addresses, string self, IEnumerable<MailboxAddress> already)
    {
        var seen = new HashSet<string>(already.Select(a => a.Address), StringComparer.OrdinalIgnoreCase) { self };
        return addresses.Where(a => !string.IsNullOrWhiteSpace(a.Address) && seen.Add(a.Address));
    }

    private static string Prefixed(string prefix, string subject, string[] known)
    {
        var trimmed = (subject ?? string.Empty).Trim();
        return known.Any(k => trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ? trimmed : prefix + trimmed;
    }
}
