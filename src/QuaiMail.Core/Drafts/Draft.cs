using System.Text.Json.Serialization;

namespace QuaiMail.Core.Drafts;

/// <summary>Nouveau message, réponse, réponse à tous ou transfert.</summary>
public enum DraftKind
{
    New,
    Reply,
    ReplyAll,
    Forward,
}

/// <summary>Pièce jointe d'un brouillon : copiée dans le dossier du brouillon (rien ne dépend du fichier d'origine).</summary>
/// <param name="StoredName">Nom du fichier dans <c>drafts/&lt;id&gt;.files</c> (fabriqué par Quai Mail).</param>
public sealed record DraftAttachment(string FileName, string StoredName, long Size);

/// <summary>
/// Message en cours d'écriture, enregistré en local (<c>drafts/&lt;id&gt;.json</c>, pièces jointes dans
/// <c>drafts/&lt;id&gt;.files</c>) : une mise à jour ou un plantage ne le perd jamais. Copié aussi dans le dossier
/// Brouillons du serveur. Il n'est envoyé que sur geste explicite (bouton Envoyer).
/// </summary>
public sealed class Draft
{
    public string Id { get; set; } = NewId();

    /// <summary>Compte d'envoi (identifiant interne).</summary>
    public string? AccountId { get; set; }

    public DraftKind Kind { get; set; } = DraftKind.New;

    public string To { get; set; } = string.Empty;

    public string Cc { get; set; } = string.Empty;

    public string Bcc { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>En réponse à ce mail (Message-Id, sans chevrons).</summary>
    public string? InReplyTo { get; set; }

    /// <summary>Fil de discussion (Message-Id des mails précédents).</summary>
    public List<string> References { get; set; } = [];

    public List<DraftAttachment> Attachments { get; set; } = [];

    /// <summary>Copie dans le dossier Brouillons du serveur (UID), à remplacer à chaque synchronisation.</summary>
    public uint? ServerUid { get; set; }

    /// <summary>Dossier Brouillons où se trouve <see cref="ServerUid"/>.</summary>
    public string? ServerFolder { get; set; }

    /// <summary>Dernier enregistrement local.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Rien n'a été écrit ni joint : il n'y a rien à garder.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(To)
        && string.IsNullOrWhiteSpace(Cc)
        && string.IsNullOrWhiteSpace(Bcc)
        && string.IsNullOrWhiteSpace(Subject)
        && string.IsNullOrWhiteSpace(Body)
        && Attachments.Count == 0;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Identifiant fabriqué par <see cref="NewId"/> (32 chiffres hexadécimaux) : sert de nom de fichier.</summary>
    public static bool IsValidId(string? id)
    {
        if (id is not { Length: 32 })
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
