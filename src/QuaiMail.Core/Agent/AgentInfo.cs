namespace QuaiMail.Core.Agent;

/// <summary>
/// Noms partagés par Quai Mail, son agent de notification (<c>QuaiMailAgent.exe</c>), l'installateur et le CI. Les
/// objets du noyau sont dans l'espace <c>Local\</c> : un agent par session Windows, jamais visible d'un autre utilisateur.
/// </summary>
public static class AgentInfo
{
    /// <summary>Exécutable de l'agent, à côté de <c>QuaiMail.exe</c>.</summary>
    public const string ExeName = "QuaiMailAgent.exe";

    /// <summary>Valeur de <c>HKCU\…\Run</c> qui lance l'agent à l'ouverture de la session (installer/QuaiMail.iss l'efface).</summary>
    public const string RunValueName = "QuaiMailAgent";

    /// <summary>Tenu par l'agent tant qu'il tourne : une seule instance, et « l'agent tourne-t-il ? ».</summary>
    public const string InstanceMutexName = @"Local\QuaiMail.Agent";

    /// <summary>Demande d'arrêt (Quai Mail avant une mise à jour, installateur, désinstallateur, réglage coupé).</summary>
    public const string QuitEventName = @"Local\QuaiMail.Agent.Quit";

    /// <summary>Tenu par la fenêtre de Quai Mail tant qu'elle est ouverte : l'agent se met en pause.</summary>
    public const string AppRunningMutexName = @"Local\QuaiMail.App.Running";

    /// <summary>Signalé par Quai Mail à son ouverture, pour réveiller l'agent (qui se met alors en pause).</summary>
    public const string AppStartedEventName = @"Local\QuaiMail.App.Started";

    /// <summary>Classe de la fenêtre cachée de l'agent (icône de la zone de notification, commandes du menu).</summary>
    public const string WindowClassName = "QuaiMail.Agent";

    /// <summary><c>QuaiMailAgent.exe --quit</c> : arrête l'agent ouvert ; code 0 s'il tournait (et s'est arrêté), 1 sinon.</summary>
    public const string QuitSwitch = "--quit";

    /// <summary>Commandes du menu de l'icône (WM_COMMAND), aussi envoyées par le CI pour les vérifier.</summary>
    public const int CommandOpen = 1;

    /// <inheritdoc cref="CommandOpen"/>
    public const int CommandToggleAutoStart = 2;

    /// <inheritdoc cref="CommandOpen"/>
    public const int CommandQuit = 3;
}
