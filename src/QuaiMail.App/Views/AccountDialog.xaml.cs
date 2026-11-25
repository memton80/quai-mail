using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Views;

/// <summary>
/// Ajout d'un compte : « Ajouter » vérifie les réglages par une vraie connexion (aucun mail envoyé) et n'enregistre
/// qu'en cas de succès ; sinon le dialogue reste ouvert avec l'explication.
/// </summary>
public sealed partial class AccountDialog : ContentDialog
{
    public AccountDialog(AccountEditorViewModel editor)
    {
        Editor = editor;
        InitializeComponent();
        Opened += (_, _) => AddressBox.Focus(FocusState.Programmatic);
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
