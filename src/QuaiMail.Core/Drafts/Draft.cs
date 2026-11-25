using System.Text.Json.Serialization;

namespace QuaiMail.Core.Drafts;

/// <summary>
/// Message en cours d'écriture, enregistré en local (<c>drafts/&lt;id&gt;.json</c>) : une mise à jour ou un plantage ne
/// le perd jamais. Il n'est envoyé que sur geste explicite (bouton Envoyer).
/// </summary>
public sealed class Draft
{
    public string Id { get; set; } = NewId();

    /// <summary>Compte d'envoi (identifiant interne).</summary>
    public string? AccountId { get; set; }

    public string To { get; set; } = string.Empty;

    public string Cc { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Dernier enregistrement local.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Rien n'a été écrit : il n'y a rien à garder.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(To)
        && string.IsNullOrWhiteSpace(Cc)
        && string.IsNullOrWhiteSpace(Subject)
        && string.IsNullOrWhiteSpace(Body);

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
