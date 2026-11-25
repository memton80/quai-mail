namespace QuaiMail.Core.Mail;

/// <summary>Rôle particulier d'un dossier (annoncé par le serveur, extension SPECIAL-USE, ou boîte de réception).</summary>
public enum FolderRole
{
    None,
    Inbox,
    Drafts,
    Sent,
    Archive,
    Junk,
    Trash,
}

/// <summary>Un dossier IMAP.</summary>
/// <param name="FullName">Chemin complet côté serveur (« INBOX », « Archives/2026 ») : identifiant du dossier.</param>
/// <param name="Name">Nom affiché (dernier élément du chemin).</param>
/// <param name="Depth">Niveau dans l'arborescence (0 = racine).</param>
/// <param name="CanSelect">Faux pour un simple conteneur (\Noselect) : pas de mails dedans.</param>
public sealed record MailFolderInfo(string FullName, string Name, int Depth, FolderRole Role, bool CanSelect, int Unread, int Total);

/// <summary>En-tête d'un mail dans une liste (rien du corps).</summary>
public sealed record MessageHeader(
    uint Uid,
    string Subject,
    string FromName,
    string FromAddress,
    DateTimeOffset Date,
    bool IsRead,
    bool IsFlagged,
    long Size)
{
    /// <summary>Expéditeur tel qu'affiché : son nom, sinon son adresse.</summary>
    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
}

/// <summary>Une page d'en-têtes, du plus récent au plus ancien.</summary>
/// <param name="UidValidity">Valeur UIDVALIDITY du dossier : si elle change, les identifiants en cache ne valent plus rien.</param>
/// <param name="Total">Nombre total de mails dans le dossier.</param>
public sealed record MessagePage(uint UidValidity, int Total, IReadOnlyList<MessageHeader> Messages)
{
    public static MessagePage Empty { get; } = new(0, 0, []);
}
