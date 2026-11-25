namespace QuaiMail.Core.Services;

/// <summary>Ce que Windows a fait d'une pièce jointe enregistrée.</summary>
public enum SavedFileVerdict
{
    /// <summary>Marquée « venue d'Internet » : à l'ouverture, SmartScreen, l'antivirus et le mode protégé d'Office s'appliquent.</summary>
    Marked,

    /// <summary>Bloquée par l'antivirus ou par la stratégie de sécurité de Windows : le fichier ne doit pas rester.</summary>
    Blocked,

    /// <summary>Marque impossible (clé USB en FAT32, certains partages réseau) : fichier gardé, sans la protection de Windows.</summary>
    Unmarked,
}

/// <summary>
/// « Marque du web » d'une pièce jointe enregistrée, comme un navigateur ou Outlook : Windows sait que le fichier vient
/// d'Internet, le fait vérifier par l'antivirus et applique ses protections quand on l'ouvre. Implémentée par
/// l'application (Windows) ; le fichier est déjà complet sur le disque quand elle est appelée.
/// </summary>
public interface IAttachmentSafety
{
    Task<SavedFileVerdict> MarkAsync(string path, CancellationToken cancellationToken = default);
}
