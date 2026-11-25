using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.App.Localization;
using QuaiMail.App.Views;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Services;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Services;

/// <summary>
/// Boîtes de dialogue WinUI : une seule à la fois (contrainte de <see cref="ContentDialog"/>),
/// toujours affichées depuis le fil d'interface quel que soit le fil appelant.
/// </summary>
internal sealed class DialogService : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ContentDialogResult? _chosen;

    /// <summary>Racine de la fenêtre principale ; renseignée par celle-ci à sa création.</summary>
    public FrameworkElement? Root { get; set; }

    /// <summary>Dialogue actuellement affiché (utilisé par le test de fumée).</summary>
    public ContentDialog? Current { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) =>
        OnUiAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirmText,
                CloseButtonText = Loc.Get("Common_Cancel"),
                // Action irréversible : « Annuler » reste le bouton par défaut.
                DefaultButton = isDestructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };

            return await ShowAsync(dialog) == ContentDialogResult.Primary;
        });

    public Task ShowErrorAsync(string title, string message) =>
        OnUiAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                CloseButtonText = Loc.Get("Common_Close"),
                DefaultButton = ContentDialogButton.Close,
            };

            await ShowAsync(dialog);
            return true;
        });

    /// <summary>
    /// « Garder ce brouillon ? » : Garder (bouton par défaut, enregistré sur cet ordinateur), Supprimer, ou Annuler
    /// (Échap) qui laisse le brouillon ouvert et l'action demandée en attente.
    /// </summary>
    public Task<DraftChoice> AskKeepDraftAsync(string subject) =>
        OnUiAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("KeepDraft_Title"),
                Content = new TextBlock { Text = Loc.Format("KeepDraft_Message", subject), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("KeepDraft_Keep"),
                SecondaryButtonText = Loc.Get("KeepDraft_Discard"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };

            return await ShowAsync(dialog) switch
            {
                ContentDialogResult.Primary => DraftChoice.Keep,
                ContentDialogResult.Secondary => DraftChoice.Discard,
                _ => DraftChoice.Cancel,
            };
        });

    /// <summary>Réglages (thème, mises à jour, images distantes, cache).</summary>
    public Task ShowSettingsAsync(SettingsViewModel settings, MailboxViewModel mailbox) =>
        OnUiAsync(async () =>
        {
            await ShowAsync(new SettingsDialog(settings, mailbox));
            return true;
        });

    /// <summary>Ajout d'un compte ; retourne le compte vérifié et enregistré, ou <c>null</c> si l'utilisateur renonce.</summary>
    public Task<Account?> ShowAccountDialogAsync(AccountEditorViewModel editor) =>
        OnUiAsync(async () =>
        {
            await ShowAsync(new AccountDialog(editor));
            return editor.SavedAccount;
        });

    /// <summary>
    /// Test de fumée : répond au dialogue affiché comme l'aurait fait un clic sur l'un de ses boutons
    /// (<see cref="ContentDialog.Hide"/> seul vaut « Annuler »).
    /// </summary>
    public void Choose(ContentDialogResult result)
    {
        if (Current is { } dialog)
        {
            _chosen = result;
            dialog.Hide();
        }
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var queue = (Root ?? throw new InvalidOperationException("La fenêtre principale n'est pas prête.")).DispatcherQueue;
        if (queue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = queue.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!queued)
        {
            completion.TrySetException(new InvalidOperationException("Le fil d'interface n'accepte plus de travail."));
        }

        return completion.Task;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var root = Root ?? throw new InvalidOperationException("La fenêtre principale n'est pas prête.");

        // Thème changé pendant que le dialogue est ouvert (depuis les réglages eux-mêmes) : le dialogue suit la fenêtre.
        void FollowTheme(FrameworkElement sender, object args) => dialog.RequestedTheme = sender.ActualTheme;

        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = root.XamlRoot;
            dialog.RequestedTheme = root.ActualTheme;
            root.ActualThemeChanged += FollowTheme;
            if (dialog.Style is null && Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style))
            {
                dialog.Style = style as Style;
            }

            _chosen = null;
            Current = dialog;
            var result = await dialog.ShowAsync();
            return _chosen ?? result;
        }
        finally
        {
            root.ActualThemeChanged -= FollowTheme;
            Current = null;
            _chosen = null;
            _gate.Release();
        }
    }
}
