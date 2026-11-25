namespace QuaiMail.Core.Mail;

/// <summary>
/// Ordre d'affichage des dossiers : boîte de réception, brouillons, envoyés, archives, indésirables, corbeille, puis les
/// autres dossiers dans l'ordre alphabétique, chaque sous-dossier juste sous son parent.
/// </summary>
public static class FolderOrder
{
    public static List<MailFolderInfo> Sort(IEnumerable<MailFolderInfo> folders)
    {
        var list = folders.ToList();
        var special = list.Where(f => f.Role != FolderRole.None && f.Depth == 0).OrderBy(f => (int)f.Role).ThenBy(f => f.FullName, StringComparer.CurrentCultureIgnoreCase);
        var others = list.Where(f => f.Role == FolderRole.None || f.Depth > 0).OrderBy(f => f.FullName, StringComparer.CurrentCultureIgnoreCase);
        return [.. special, .. others];
    }
}
