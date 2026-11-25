using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Views;

/// <summary>Réglages : appliqués et enregistrés immédiatement, sans bouton « Enregistrer ».</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly MailboxViewModel _mailbox;

    public SettingsDialog(SettingsViewModel settings, MailboxViewModel mailbox)
    {
        Settings = settings;
        _mailbox = mailbox;
        InitializeComponent();
    }

    public SettingsViewModel Settings { get; }

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
