namespace QuaiMail.Core.Agent;

/// <summary>
/// L'agent de notification vu des réglages de Quai Mail (implémenté par l'application ; le test de fumée en a un faux
/// qui ne touche ni au registre ni aux processus).
/// </summary>
public interface IBackgroundAgent
{
    /// <summary><c>QuaiMailAgent.exe</c> est livré à côté de Quai Mail (absent d'une compilation de développement).</summary>
    bool IsAvailable { get; }

    /// <summary>L'agent se lance à l'ouverture de la session Windows.</summary>
    bool IsAutoStartEnabled { get; }

    void SetAutoStart(bool enabled);

    /// <summary>Arrête l'agent s'il tourne (réglage coupé).</summary>
    void Stop();
}
