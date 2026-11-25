namespace QuaiMail.Core.Updates;

/// <summary>Côté application : ce que QuaiMail.Core ne fait pas lui-même (registre, processus, fenêtre, navigateur).</summary>
public interface IUpdateInstaller
{
    /// <summary>Quai Mail tourne depuis son installation : l'installateur peut le mettre à jour (pas une copie portable).</summary>
    bool CanInstall { get; }

    /// <summary>
    /// Propose de garder le brouillon en cours (enregistré en local), lance l'installateur vérifié en mode silencieux puis quitte
    /// Quai Mail ; l'installateur relance la nouvelle version une fois installée, qui restaure l'état de la fenêtre.
    /// </summary>
    /// <returns><c>false</c> si l'utilisateur annule (brouillon gardé ouvert) : rien n'est lancé.</returns>
    Task<bool> InstallAndRestartAsync(string installerPath);

    /// <summary>Ouvre la page de la release dans le navigateur (copie portable, installateur absent de la release).</summary>
    void OpenReleasePage(Uri page);
}
