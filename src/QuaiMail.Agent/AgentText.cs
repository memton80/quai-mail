namespace QuaiMail.Agent;

/// <summary>
/// Textes de l'agent (français, comme Quai Mail). Peu nombreux et sans fichier de ressources : l'agent n'embarque pas
/// le système de ressources de WinUI (voir docs/DECISIONS.md).
/// </summary>
internal static class AgentText
{
    public const string WindowTitle = "Quai Mail (agent de notification)";

    public const string Open = "Ouvrir Quai Mail";

    public const string StartWithWindows = "Lancer avec Windows";

    public const string Quit = "Arrêter l'agent";

    public const string NoSubject = "(sans objet)";

    public const string TipStarting = "Quai Mail : démarrage de l'agent";

    public const string TipPaused = "Quai Mail est ouvert : l'agent attend sa fermeture";

    public static string TipWatching(int accounts) =>
        accounts == 1 ? "Quai Mail : surveille vos nouveaux mails (1 compte)" : $"Quai Mail : surveille vos nouveaux mails ({accounts} comptes)";

    public const string StatusStarting = "Démarrage…";

    public const string StatusPaused = "En pause : Quai Mail est ouvert";

    public static string StatusWatching(int accounts) =>
        accounts == 1 ? "Surveille 1 compte" : $"Surveille {accounts} comptes";
}
