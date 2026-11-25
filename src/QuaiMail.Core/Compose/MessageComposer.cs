using MimeKit;
using MimeKit.Utils;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Drafts;

namespace QuaiMail.Core.Compose;

/// <summary>Le mail MIME d'un brouillon (envoi, copie dans Brouillons) : texte brut UTF-8, pièces jointes copiées.</summary>
public static class MessageComposer
{
    /// <param name="forSending">Vrai pour l'envoi : au moins un destinataire, et toutes les adresses valides.</param>
    /// <exception cref="InvalidRecipientsException">Adresses invalides, ou aucun destinataire pour un envoi.</exception>
    public static MimeMessage Build(Account account, Draft draft, DraftStore store, bool forSending)
    {
        var to = Recipients.Parse(draft.To, out var invalidTo);
        var cc = Recipients.Parse(draft.Cc, out var invalidCc);
        var bcc = Recipients.Parse(draft.Bcc, out var invalidBcc);
        var invalid = invalidTo.Concat(invalidCc).Concat(invalidBcc).ToList();
        if (forSending && (invalid.Count > 0 || to.Count + cc.Count + bcc.Count == 0))
        {
            throw new InvalidRecipientsException(invalid);
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName, account.Address));
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Bcc.AddRange(bcc);
        message.Subject = draft.Subject.Trim();
        message.Date = DateTimeOffset.Now;
        message.MessageId = MimeUtils.GenerateMessageId(MailAddressRules.TryGetDomain(account.Address, out var domain) ? domain : "quaimail.invalid");
        if (draft.InReplyTo is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
        }

        foreach (var reference in draft.References)
        {
            message.References.Add(reference);
        }

        var builder = new BodyBuilder { TextBody = draft.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) };
        foreach (var attachment in draft.Attachments)
        {
            var path = store.AttachmentPath(draft, attachment) ?? throw new InvalidOperationException("Pièce jointe introuvable.");
            builder.Attachments.Add(attachment.FileName, File.ReadAllBytes(path));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }
}

/// <summary>Destinataires manquants ou invalides : rien n'est envoyé.</summary>
public sealed class InvalidRecipientsException(IReadOnlyList<string> invalid)
    : Exception(invalid.Count == 0 ? "Aucun destinataire." : "Adresses invalides : " + string.Join(", ", invalid))
{
    /// <summary>Morceaux saisis qui ne sont pas des adresses (vide : il n'y a simplement aucun destinataire).</summary>
    public IReadOnlyList<string> Invalid { get; } = invalid;
}
