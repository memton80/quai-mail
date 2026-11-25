namespace QuaiMail.Core.Services;

/// <summary>Réponse à « Garder ce brouillon ? ».</summary>
public enum DraftChoice
{
    /// <summary>Enregistré sur cet ordinateur (et rouvert après une mise à jour).</summary>
    Keep,

    /// <summary>Supprimé.</summary>
    Discard,

    /// <summary>Rien ne se passe : le brouillon reste ouvert, l'action demandée (fermer, mettre à jour) attend.</summary>
    Cancel,
}

/// <summary>Boîtes de dialogue de l'application, vues depuis les view-models (implémentées par l'interface WinUI).</summary>
public interface IDialogService
{
    /// <summary>Demande une confirmation ; <paramref name="isDestructive"/> signale une action irréversible.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);

    Task ShowErrorAsync(string title, string message);

    /// <summary>« Garder ce brouillon ? » : brouillon non envoyé au moment de quitter ou de se mettre à jour.</summary>
    /// <param name="subject">Objet du brouillon (ou « (sans objet) »), pour que l'utilisateur sache lequel.</param>
    Task<DraftChoice> AskKeepDraftAsync(string subject);
}
