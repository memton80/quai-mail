namespace QuaiMail.Core.Models;

public enum AppTheme
{
    /// <summary>Suit le thème de Windows.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Réglages de l'application (fichier <c>settings.json</c>).</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Images distantes des mails bloquées par défaut (confidentialité : pas de pixel espion). Vrai par défaut.</summary>
    public bool BlockRemoteImages { get; set; } = true;

    /// <summary>Notification de Windows à l'arrivée d'un mail (quand la fenêtre n'est pas au premier plan).</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// Agent de notification (<c>QuaiMailAgent.exe</c>, zone de notification) : quand Quai Mail est fermé, il surveille
    /// les boîtes de réception et annonce les nouveaux mails. Démarré à la fermeture de Quai Mail. Vrai par défaut.
    /// </summary>
    public bool BackgroundAgent { get; set; } = true;

    /// <summary>Au démarrage, demander à GitHub si une version plus récente est publiée (comme Quai).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Mail signé avec une clé inconnue : la demander à keys.openpgp.org (empreinte seulement) pour vérifier la
    /// signature. Désactivé, la recherche se fait seulement sur demande (bouton du bandeau).
    /// </summary>
    public bool LookUpPgpKeys { get; set; } = true;

    /// <summary>
    /// Comptes repliés dans le volet de gauche (identifiants internes, jamais les adresses). Tableau remplacé à chaque
    /// changement, jamais modifié en place : <see cref="Clone"/> peut donc le partager.
    /// </summary>
    public string[] CollapsedAccounts { get; set; } = [];

    /// <summary>Volet de gauche réduit en rail d'icônes (bouton « menu » en haut du volet). Déplié par défaut.</summary>
    public bool NavPaneCompact { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Ramène les valeurs dans leurs bornes (fichier modifié à la main, ancienne version…).</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.System;
        }

        CollapsedAccounts = CollapsedAccounts is null
            ? []
            : [.. CollapsedAccounts.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)];
    }
}
