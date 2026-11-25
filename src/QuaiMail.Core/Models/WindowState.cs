using System.Text.Json.Serialization;

namespace QuaiMail.Core.Models;

/// <summary>
/// Ce que montre la fenêtre (compte, dossier, mail ouvert, brouillon en cours), noté juste avant une mise à jour dans
/// <c>updates/reopen.txt</c> et restauré une fois par la nouvelle version. Uniquement des identifiants : ni objet, ni
/// corps, ni adresse (le brouillon lui-même est dans le dossier des brouillons locaux).
/// </summary>
public sealed record WindowState
{
    /// <summary>Identifiant interne du compte sélectionné (jamais l'adresse).</summary>
    public string? AccountId { get; init; }

    /// <summary>Chemin IMAP du dossier affiché (« INBOX », « Archives/2026 »).</summary>
    public string? FolderPath { get; init; }

    /// <summary>Identifiant du mail ouvert dans le volet de lecture.</summary>
    public string? MessageId { get; init; }

    /// <summary>Brouillon gardé (voir <see cref="Drafts.DraftStore"/>) à rouvrir.</summary>
    public string? DraftId { get; init; }

    [JsonIgnore]
    public bool IsEmpty => AccountId is null && FolderPath is null && MessageId is null && DraftId is null;
}
