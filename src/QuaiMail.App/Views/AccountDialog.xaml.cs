using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Views;

/// <summary>
/// Ajout d'un compte : « Ajouter » vérifie les réglages par une vraie connexion (aucun mail envoyé) et n'enregistre
/// qu'en cas de succès ; sinon le dialogue reste ouvert avec l'explication. « Se connecter avec Google / Microsoft »
/// ouvre la page officielle dans le navigateur ; fermer le dialogue abandonne l'attente.
/// </summary>
public sealed partial class AccountDialog : ContentDialog
{
    private CancellationTokenSource? _signIn;

    public AccountDialog(AccountEditorViewModel editor)
    {
        Editor = editor;
        InitializeComponent();
        Opened += (_, _) => AddressBox.Focus(FocusState.Programmatic);
        Closing += (_, _) => _signIn?.Cancel();
    }

    /// <summary>Test de fumée : clic sur « Se connecter avec Google », comme le ferait un utilisateur.</summary>
    internal void ClickSignInWithGoogle() => new ButtonAutomationPeer(GoogleButton).Invoke();

    private void OnSignInGoogleClick(object sender, RoutedEventArgs e) => _ = SignInAsync(AccountAuth.Google);

    private void OnSignInMicrosoftClick(object sender, RoutedEventArgs e) => _ = SignInAsync(AccountAuth.Microsoft);

    private async Task SignInAsync(AccountAuth auth)
    {
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        try
        {
            if (await Editor.SignInAsync(auth, _signIn.Token) is not null)
            {
                Hide();
            }
        }
        catch (OperationCanceledException)
        {
            // Dialogue fermé pendant l'attente : rien n'est enregistré.
        }
        catch (Exception)
        {
            // L'éditeur a déjà montré l'erreur ; le dialogue reste ouvert.
        }
    }

    public AccountEditorViewModel Editor { get; }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = await Editor.SaveAsync() is null;
        }
        catch (Exception)
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>L'adresse saisie : les réglages sont cherchés tout de suite (fournisseur connu, autoconfiguration).</summary>
    private async void OnAddressLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MailAddressRules.IsValidAddress(Editor.Address))
            {
                await Editor.DetectAsync();
            }
        }
        catch (Exception)
        {
            // La détection est un confort : les réglages restent saisissables à la main.
        }
    }
}
