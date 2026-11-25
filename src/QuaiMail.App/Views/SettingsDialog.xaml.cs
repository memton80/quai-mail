using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Views;

/// <summary>Réglages : appliqués et enregistrés immédiatement, sans bouton « Enregistrer ».</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly MailboxViewModel _mailbox;

    public SettingsDialog(SettingsViewModel settings, MailboxViewModel mailbox, PgpKeysViewModel keys)
    {
        Settings = settings;
        Keys = keys;
        _mailbox = mailbox;
        InitializeComponent();
    }

    public SettingsViewModel Settings { get; }

    /// <summary>Clés OpenPGP pour signer les mails.</summary>
    public PgpKeysViewModel Keys { get; }

    private async void OnImportKeyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Keys.ImportAsync();
        }
        catch (Exception)
        {
            // L'erreur prévisible est déjà affichée par le view-model.
        }
    }

    private void OnForgetPassphrasesClick(object sender, RoutedEventArgs e) => Keys.ForgetPassphrases();

    /// <summary>« Retirer » du message de confirmation, ouvert par le bouton de la clé.</summary>
    private void OnRemoveKeyConfirmed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PgpKeyInfo key)
        {
            Keys.Remove(key);
        }
    }

    /// <summary>Efface le cache local : tout est relu sur les serveurs, rien n'est perdu.</summary>
    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _mailbox.ClearCacheAsync();
        }
        catch (Exception)
        {
            // Le cache se reconstruira au prochain affichage.
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.LogsDirectory}\"") { UseShellExecute = true });
    }
}
