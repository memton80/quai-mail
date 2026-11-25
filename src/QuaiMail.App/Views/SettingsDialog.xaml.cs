using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.ViewModels;
using Windows.System;

namespace QuaiMail.App.Views;

/// <summary>Réglages : appliqués et enregistrés immédiatement, sans bouton « Enregistrer ».</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly MailboxViewModel _mailbox;

    public SettingsDialog(SettingsViewModel settings, MailboxViewModel mailbox, PgpKeysViewModel keys, TrustedSendersViewModel trusted)
    {
        Settings = settings;
        Keys = keys;
        Trusted = trusted;
        _mailbox = mailbox;
        InitializeComponent();

        // Les view-models vivent plus longtemps que le dialogue : une fois fermé, il ne doit plus suivre leurs changements
        // (sinon la liste des clés d'un dialogue fermé reçoit encore les ajouts et retraits, et plante).
        Closed += (_, _) =>
        {
            KeysRepeater.ItemsSource = null;
            GnuPgRepeater.ItemsSource = null;
            TrustedRepeater.ItemsSource = null;
            Bindings.StopTracking();
        };

        // GnuPG (Kleopatra) cherché à chaque ouverture : ses clés de signature s'affichent, prêtes à être importées.
        Opened += async (_, _) =>
        {
            try
            {
                await Keys.DetectAsync();
            }
            catch (Exception)
            {
                // Sans GnuPG lisible, sa partie reste cachée ; l'import d'un fichier reste possible.
            }
        };
    }

    /// <summary>Petit menu « phrase de passe » ouvert (un seul à la fois), refermé une fois l'import tenté.</summary>
    private Flyout? _gnupgFlyout;

    public SettingsViewModel Settings { get; }

    /// <summary>Clés OpenPGP pour signer les mails.</summary>
    public PgpKeysViewModel Keys { get; }

    /// <summary>Expéditeurs sûrs (lus sans le mode méfiant).</summary>
    public TrustedSendersViewModel Trusted { get; }

    private void OnRemoveTrustedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrustedSenderItem item)
        {
            Trusted.Remove(item);
        }
    }

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

    private void OnGnuPgFlyoutOpening(object? sender, object e) => _gnupgFlyout = sender as Flyout;

    /// <summary>« Importer » (ou « Mettre à jour ») du petit menu d'une clé de GnuPG.</summary>
    private async void OnImportFromGnuPgConfirmed(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GnuPgKeyItem item, Parent: Panel panel })
        {
            await ImportFromGnuPgAsync(item, panel);
        }
    }

    /// <summary>Entrée dans le champ de la phrase de passe : comme le bouton « Importer ».</summary>
    private async void OnGnuPgPassphraseKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is PasswordBox { Parent: Panel panel }
            && panel.Children.OfType<Button>().FirstOrDefault() is { Tag: GnuPgKeyItem item })
        {
            e.Handled = true;
            await ImportFromGnuPgAsync(item, panel);
        }
    }

    /// <summary>
    /// Copie la clé dans Quai Mail ; le champ est vidé aussitôt (la phrase de passe n'est gardée nulle part) et le menu
    /// se referme : le résultat, ou l'erreur, s'affiche sous les boutons de la section.
    /// </summary>
    private async Task ImportFromGnuPgAsync(GnuPgKeyItem item, Panel panel)
    {
        var box = panel.Children.OfType<PasswordBox>().FirstOrDefault();
        var passphrase = box?.Password;
        if (box is not null)
        {
            box.Password = string.Empty;
        }

        panel.IsHitTestVisible = false;
        try
        {
            await Keys.ImportFromGnuPgAsync(item, passphrase);
        }
        catch (Exception)
        {
            // L'erreur prévisible est déjà affichée par le view-model.
        }
        finally
        {
            panel.IsHitTestVisible = true;
            _gnupgFlyout?.Hide();
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
