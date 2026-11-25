using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuaiMail.App.Views;

/// <summary>
/// Phrase de passe de la clé OpenPGP, au moment d'envoyer un message signé. Elle n'est jamais enregistrée : Quai Mail
/// la garde en mémoire jusqu'à sa fermeture.
/// </summary>
public sealed partial class PassphraseDialog : ContentDialog
{
    public PassphraseDialog(string message, bool retry)
    {
        Message = message;
        Retry = retry;
        InitializeComponent();
        Opened += (_, _) => PassphraseBox.Focus(FocusState.Programmatic);
    }

    public string Message { get; }

    public bool Retry { get; }

    public string Passphrase
    {
        get => PassphraseBox.Password;
        set => PassphraseBox.Password = value;
    }
}
