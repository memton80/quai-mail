namespace QuaiMail.Core.Services;

/// <summary>Réponse à « Garder ce brouillon ? ».</summary>
public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Boîtes de dialogue de l'application, vues depuis les view-models (implémentées par l'interface WinUI).</summary>
public interface IDialogService
{
    /// <summary>Demande une confirmation ; <paramref name="isDestructive"/> signale une action irréversible.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Brouillon non envoyé au moment de quitter ou de se mettre à jour : garder, abandonner ou annuler.</summary>
    Task<SaveChoice> AskSaveDraftAsync(string subject);
}
