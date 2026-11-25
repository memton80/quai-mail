namespace QuaiMail.Core.Safety;

/// <summary>Confiance dans l'expéditeur du mail affiché.</summary>
public enum SenderStatus
{
    /// <summary>Aucun mail affiché (ou pas encore chargé).</summary>
    None,

    /// <summary>Expéditeur sûr ou soi-même, contrôles d'authentification sans échec : affichage normal, images comprises.</summary>
    Trusted,

    /// <summary>Expéditeur inconnu, rien de suspect relevé : mode méfiant discret.</summary>
    Unknown,

    /// <summary>Expéditeur inconnu et indices de hameçonnage : avertissements.</summary>
    Suspicious,

    /// <summary>Contrôles d'authentification en échec : expéditeur peut-être usurpé (même s'il est dans la liste).</summary>
    Spoofed,
}
