using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using QuaiMail.App.Localization;
using QuaiMail.App.Services;
using QuaiMail.App.Views;
using QuaiMail.Core;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Safety;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Updates;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App;

/// <summary>
/// Test de fumée lancé par le CI : <c>QuaiMail.exe --smoke-test &lt;dossier&gt;</c>.
/// Ouvre la fenêtre, les réglages, le thème sombre, un brouillon simulé et la question « Garder ce brouillon ? »,
/// l'état de la fenêtre noté puis restauré, l'ajout d'un compte (serveur de test en mémoire, coffre de Windows), ses
/// dossiers et ses mails par pages, le cache vidé, le retrait du compte, le bandeau de mise à jour ; enregistre des
/// captures, puis quitte avec le code 0 (fichier <c>smoke-ok.txt</c>) ou 1 (fichier <c>smoke-error.txt</c>). Données
/// dans un dossier jetable ; jamais de vrai compte mail, aucune connexion réseau.
/// </summary>
internal static class SmokeTest
{
    private const string Switch = "--smoke-test";
    private const string UpdateSwitch = "--update-test";

    /// <summary>Version « plus récente » des tests : jamais publiée pour de vrai.</summary>
    public const string ReleaseTag = "v99.0.0";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    public static bool TryGetOutputDirectory(out string directory)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            directory = string.Empty;
            return false;
        }

        directory = index + 1 < args.Length
            ? Path.GetFullPath(args[index + 1])
            : Path.Combine(Path.GetTempPath(), "quaimail-smoke");
        return true;
    }

    /// <summary>
    /// Mise à jour en place (CI, repris de Quai) : <c>QuaiMail.exe --update-test &lt;dossier&gt; &lt;installateur&gt;</c>.
    /// Quai Mail, installé, avec un brouillon ouvert et un état de fenêtre, se voit proposer cet installateur comme
    /// nouvelle version, le télécharge, le vérifie, garde le brouillon, le lance en mode silencieux et se ferme ; le
    /// workflow vérifie ensuite que l'installateur a réussi, a relancé Quai Mail, et que l'état de la fenêtre (brouillon
    /// compris) a été restauré.
    /// </summary>
    public static bool TryGetUpdateTest(out string directory, out string installer)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, UpdateSwitch, StringComparison.OrdinalIgnoreCase));
        var found = index >= 0 && index + 2 < args.Length;
        directory = found ? Path.GetFullPath(args[index + 1]) : string.Empty;
        installer = found ? Path.GetFullPath(args[index + 2]) : string.Empty;
        return found;
    }

    public static void IsolateData(string directory) =>
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, Path.Combine(directory, "data"));

    public static async Task RunAsync(MainWindow window, IServiceProvider services, ILogger logger, string directory)
    {
        var report = new List<string>();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Delay(1500);
            Check(window.Title == AppInfo.DisplayName, $"titre de la fenêtre « {window.Title} »");
            Check(Loc.Get("App_Title") == AppInfo.DisplayName, "ressources .resw non chargées (clé affichée telle quelle)");
            await CaptureAsync(hwnd, directory, "1-accueil.png", report);

            var dialogs = services.GetRequiredService<DialogService>();
            var settings = services.GetRequiredService<SettingsViewModel>();
            var shell = services.GetRequiredService<ShellViewModel>();

            // Réglages, puis thème sombre.
            var settingsDialog = window.ShowSettingsAsync();
            await WaitForAsync(() => dialogs.Current is not null, "ouverture des réglages");
            await Task.Delay(800);
            await CaptureAsync(hwnd, directory, "2-reglages.png", report);
            dialogs.Current?.Hide();
            await WithTimeout(settingsDialog, "fermeture des réglages");
            Check(settings.BlockRemoteImages, "images distantes bloquées par défaut");
            Check(settings.CheckForUpdates, "recherche des mises à jour active par défaut");

            settings.ThemeIndex = 2;
            await Task.Delay(1000);
            await CaptureAsync(hwnd, directory, "3-sombre.png", report);
            settings.ThemeIndex = 0;

            await DraftAsync(shell, dialogs, services.GetRequiredService<DraftStore>(), hwnd, directory, report);
            await WindowStateAsync(shell, services.GetRequiredService<AppPaths>(), report);
            await AccountsAsync(window, services, dialogs, hwnd, directory, report);
            await BrowserSignInAsync(window, services, dialogs, hwnd, directory, report);
            await UpdateBannerAsync(window, services, hwnd, directory, report);

            report.Add("Test de fumée : OK");
            File.WriteAllLines(Path.Combine(directory, "smoke-report.txt"), report);
            File.WriteAllText(Path.Combine(directory, "smoke-ok.txt"), "ok");
            logger.LogInformation("Test de fumée réussi");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test de fumée en échec");
            report.Add("ÉCHEC : " + ex.Message);
            TryWrite(Path.Combine(directory, "smoke-report.txt"), string.Join(Environment.NewLine, report));
            try
            {
                await CaptureAsync(hwnd, directory, "echec.png", report);
            }
            catch (Exception)
            {
                // La capture d'échec est un plus : son absence ne change rien au diagnostic.
            }

            Fail(directory, ex);
        }
    }

    /// <summary>
    /// Mise à jour en place : brouillon ouvert et état de fenêtre simulés, bandeau, téléchargement vérifié,
    /// « Garder ce brouillon ? » (Garder), installation silencieuse, fermeture. La suite (installateur, relance,
    /// état restauré) est vérifiée par le workflow.
    /// </summary>
    public static async Task RunUpdateAsync(MainWindow window, IServiceProvider services, ILogger logger, string directory)
    {
        var report = new List<string>();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Delay(1500);

            // État de la fenêtre et brouillon en cours pendant la mise à jour : la nouvelle version doit les restaurer.
            var shell = services.GetRequiredService<ShellViewModel>();
            shell.SelectedAccountId = "compte-de-test";
            shell.SelectedFolderPath = "INBOX";
            shell.OpenMessageId = "1";
            var draft = new Draft { Subject = "Brouillon gardé pendant la mise à jour", Body = "Texte du test de la mise à jour." };
            shell.OpenDraft(draft);

            var banner = services.GetRequiredService<UpdateBannerViewModel>();
            await banner.CheckAsync();
            Check(banner.IsOpen, "le bandeau ne propose pas la nouvelle version");
            Check(banner.CanInstallInPlace, "Quai Mail, lancé depuis son installation, ne propose pas la mise à jour en place (installation non reconnue ?)");
            await Task.Delay(500);
            await CaptureAsync(hwnd, directory, "update-available.png", report);

            // Un clic : téléchargement (capturé en cours), vérification, « Garder ce brouillon ? », installation, fermeture.
            var dialogs = services.GetRequiredService<DialogService>();
            var update = banner.ActAsync();
            await WaitForAsync(() => !banner.IsDownloading || banner.Progress >= 25, "téléchargement de la mise à jour");
            if (banner.IsDownloading)
            {
                await CaptureAsync(hwnd, directory, "update-downloading.png", report);
            }

            await WaitForAsync(() => dialogs.Current is not null || update.IsCompleted, "question « Garder ce brouillon ? » avant la mise à jour");
            Check(dialogs.Current is not null, $"pas de question sur le brouillon avant la mise à jour ({banner.Message})");
            Check(dialogs.Current!.Title as string == Loc.Get("KeepDraft_Title"), $"question inattendue : « {dialogs.Current.Title} »");
            await Task.Delay(500);
            await CaptureAsync(hwnd, directory, "update-draft.png", report);

            logger.LogInformation("Test de la mise à jour : brouillon gardé, installation lancée");
            dialogs.Choose(ContentDialogResult.Primary);
            await update;
            Check(!banner.HasFailed, $"mise à jour en échec : {banner.Message}");

            // Normalement, Quai Mail est déjà en train de se fermer pour laisser la place à l'installateur.
            File.WriteAllLines(Path.Combine(directory, "smoke-report.txt"), report);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test de la mise à jour en échec");
            TryWrite(Path.Combine(directory, "smoke-report.txt"), string.Join(Environment.NewLine, report));
            Fail(directory, ex);
        }
    }

    /// <summary>
    /// Brouillon simulé (aucun mail n'est envoyé) : carte « Brouillon en cours », puis « Garder ce brouillon ? » :
    /// Annuler le laisse ouvert, Garder l'enregistre sur le disque.
    /// </summary>
    private static async Task DraftAsync(ShellViewModel shell, DialogService dialogs, DraftStore drafts, IntPtr hwnd, string directory, List<string> report)
    {
        var draft = new Draft { Subject = "Brouillon du test de fumée", Body = "Bonjour,\n\nTexte de test." };
        shell.OpenDraft(draft);
        Check(shell.HasDraft && shell.DraftTitle == draft.Subject, $"brouillon ouvert : « {shell.DraftTitle} »");
        await Task.Delay(600);
        await CaptureAsync(hwnd, directory, "4-brouillon.png", report);

        // Annuler : rien ne change.
        var closing = shell.ConfirmCloseDraftAsync();
        await WaitForAsync(() => dialogs.Current is not null, "question « Garder ce brouillon ? »");
        Check(dialogs.Current!.Title as string == Loc.Get("KeepDraft_Title"), $"titre de la question : « {dialogs.Current.Title} »");
        await Task.Delay(600);
        await CaptureAsync(hwnd, directory, "5-garder-brouillon.png", report);
        dialogs.Choose(ContentDialogResult.None);
        Check(!await WithTimeout(closing, "Annuler"), "« Annuler » doit laisser la fenêtre ouverte");
        Check(shell.CurrentDraft == draft, "brouillon toujours ouvert après « Annuler »");
        Check(drafts.Load(draft.Id) is null, "rien d'enregistré après « Annuler »");

        // Garder : enregistré sur le disque.
        closing = shell.ConfirmCloseDraftAsync();
        await WaitForAsync(() => dialogs.Current is not null, "question « Garder ce brouillon ? » (2)");
        dialogs.Choose(ContentDialogResult.Primary);
        Check(await WithTimeout(closing, "Garder"), "« Garder » doit permettre de fermer");
        Check(drafts.Load(draft.Id)?.Body == draft.Body, "brouillon gardé relu à l'identique");
        report.Add("Brouillon : « Garder ce brouillon ? » affiché ; Annuler le laisse ouvert, Garder l'enregistre");

        drafts.Delete(draft.Id);
        shell.CurrentDraft = null;
        await Task.Delay(300);
    }

    /// <summary>État de la fenêtre noté (comme avant une mise à jour), puis restauré une seule fois.</summary>
    private static async Task WindowStateAsync(ShellViewModel shell, AppPaths paths, List<string> report)
    {
        shell.SelectedAccountId = "compte-de-test";
        shell.SelectedFolderPath = "INBOX";
        shell.OpenMessageId = "7";
        Check(await WithTimeout(shell.PrepareForUpdateAsync(), "état de la fenêtre"), "état de la fenêtre noté sans question (pas de brouillon)");
        Check(File.Exists(paths.ReopenAfterUpdateFile), "updates/reopen.txt absent");

        shell.SelectedAccountId = null;
        shell.SelectedFolderPath = null;
        shell.OpenMessageId = null;
        Check(shell.RestoreAfterUpdate(), "état de la fenêtre non restauré");
        Check(shell is { SelectedAccountId: "compte-de-test", SelectedFolderPath: "INBOX", OpenMessageId: "7" }, "état de la fenêtre restauré inexact");
        Check(!File.Exists(paths.ReopenAfterUpdateFile), "updates/reopen.txt non effacé après restauration");
        Check(!shell.RestoreAfterUpdate(), "état de la fenêtre restauré deux fois");
        report.Add("État de la fenêtre : noté dans updates/reopen.txt, restauré une fois, puis effacé");

        shell.SelectedAccountId = null;
        shell.SelectedFolderPath = null;
        shell.OpenMessageId = null;
    }

    /// <summary>
    /// Comptes (serveur de test en mémoire, aucun réseau) : formulaire, mot de passe refusé puis accepté, mot de passe
    /// dans le coffre de Windows et nulle part ailleurs, dossiers, mails par pages, sous-dossier, cache vidé et
    /// reconstruit, retrait du compte après confirmation.
    /// </summary>
    private static async Task AccountsAsync(MainWindow window, IServiceProvider services, DialogService dialogs, IntPtr hwnd, string directory, List<string> report)
    {
        var mailbox = window.Mailbox;
        var messages = window.Messages;
        await WaitForAsync(() => !mailbox.IsRefreshing, "chargement des comptes");
        Check(mailbox.HasNoAccount, "aucun compte attendu au départ");

        var adding = window.AddAccountAsync();
        await WaitForAsync(() => dialogs.Current is AccountDialog, "formulaire d'ajout de compte");
        var editor = ((AccountDialog)dialogs.Current!).Editor;
        editor.Address = "camille@example.org";
        editor.DisplayName = "Camille";
        await WithTimeout(editor.DetectAsync(), "détection des réglages");
        Check(editor is { ImapHost: "imap.example.org", ImapPort: 993d, SmtpHost: "smtp.example.org", ShowServerSettings: true }, $"réglages supposés : {editor.ImapHost}:{editor.ImapPort}");

        // Mot de passe refusé : rien n'est enregistré, l'erreur est claire.
        editor.Password = "mauvais mot de passe";
        Check(await WithTimeout(editor.SaveAsync(), "vérification refusée") is null, "un mot de passe refusé ne doit rien enregistrer");
        Check(editor.HasError && mailbox.HasNoAccount, "erreur affichée, aucun compte ajouté");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "7-ajout-compte.png", report);

        editor.Password = SmokeMailService.Password;
        Check(await WithTimeout(editor.SaveAsync(), "vérification acceptée") is not null, $"compte non enregistré : {editor.ErrorMessage}");
        dialogs.Current?.Hide();
        var account = await WithTimeout(adding, "ajout du compte") ?? throw new InvalidOperationException("Compte ajouté introuvable.");

        var credentials = services.GetRequiredService<ICredentialStore>();
        var paths = services.GetRequiredService<AppPaths>();
        Check(credentials.GetPassword(account.Id) == SmokeMailService.Password, "mot de passe absent du coffre de Windows");
        Check(!File.ReadAllText(paths.AccountsFile).Contains(SmokeMailService.Password, StringComparison.Ordinal), "mot de passe écrit dans accounts.json");
        report.Add("Compte : vérifié, enregistré sans mot de passe dans accounts.json, mot de passe dans le coffre de Windows");

        await WaitForAsync(() => mailbox.Items.Count == 7 && !messages.IsLoading && messages.Messages.Count == MessageListViewModel.PageSize, "dossiers et premiers mails");
        Check(mailbox.SelectedItem is { Role: FolderRole.Inbox }, "boîte de réception choisie après l'ajout");
        Check(messages.HasMore && messages.Messages[0].Uid == 120, "mails du plus récent au plus ancien");
        await Task.Delay(1000);
        await CaptureAsync(hwnd, directory, "8-boite-de-reception.png", report);

        await ReadingAsync(window, services, dialogs, hwnd, directory, report);
        await ComposeAsync(window, services, dialogs, hwnd, directory, report);
        await SignedMailAsync(window, services, dialogs, account, hwnd, directory, report);

        await WithTimeout(messages.LoadMoreCommand.ExecuteAsync(null), "mails plus anciens");
        Check(messages.Messages.Count == 2 * MessageListViewModel.PageSize, $"page suivante : {messages.Messages.Count} mails");

        mailbox.SelectedItem = mailbox.Items.First(i => i.Folder?.FullName == "Archives/2026");
        await WaitForAsync(() => !messages.IsLoading && messages.Messages.Count == 1 && messages.Title == "2026", "sous-dossier");
        report.Add($"Dossiers : {mailbox.Items.Count - 1}, mails par pages de {MessageListViewModel.PageSize}, sous-dossier : OK");

        // Cache : effacé, il se reconstruit depuis le serveur.
        var cache = services.GetRequiredService<MailCache>();
        Check(cache.GetFolders(account.Id).Count == 6, "dossiers en cache");
        await WithTimeout(mailbox.ClearCacheAsync(), "vider le cache");
        await WaitForAsync(() => !messages.IsLoading && messages.Messages.Count == 1, "dossier rechargé après le cache vidé");
        Check(cache.GetFolders(account.Id).Count == 6, "cache reconstruit");
        report.Add("Cache : vidé puis reconstruit depuis le serveur");

        await ComfortAsync(window, services, dialogs, account, hwnd, directory, report);

        // Retrait du compte : confirmation, puis plus rien de local (coffre compris).
        var removing = mailbox.RemoveAccountAsync(account.Id);
        await WaitForAsync(() => dialogs.Current is not null, "confirmation du retrait du compte");
        await Task.Delay(600);
        await CaptureAsync(hwnd, directory, "9-retirer-compte.png", report);
        dialogs.Choose(ContentDialogResult.Primary);
        Check(await WithTimeout(removing, "retrait du compte"), "compte non retiré");
        Check(mailbox.HasNoAccount && credentials.GetPassword(account.Id) is null && cache.GetFolders(account.Id).Count == 0, "le compte retiré a laissé des traces");
        report.Add("Retrait du compte : confirmé, réglages, mot de passe et cache effacés");
    }

    /// <summary>
    /// Lecture dans la WebView2 verrouillée, avec un mail piégé (script, cadre, formulaire, pixel espion, lien trompeur,
    /// exécutable joint) : réglages verrouillés, rien ne sort de l'hôte local, navigation forcée annulée, lien ouvert
    /// seulement après confirmation (refusée ici), images distantes seulement sur demande, pièces jointes enregistrées
    /// sur demande (l'exécutable après un avertissement, refusé ici), lu / non lu et étoile.
    /// </summary>
    private static async Task ReadingAsync(MainWindow window, IServiceProvider services, DialogService dialogs, IntPtr hwnd, string directory, List<string> report)
    {
        var view = window.Reader;
        var reader = view.Reader;
        var messages = window.Messages;
        var launcher = (SmokeLauncher)services.GetRequiredService<ILauncher>();
        var mail = (SmokeMailService)services.GetRequiredService<IMailService>();
        services.GetRequiredService<FileDialogs>().SmokeDirectory = directory;

        await WaitForAsync(() => view.IsReady, "WebView2 de lecture prête");
        Check(
            view.Settings is
            {
                IsScriptEnabled: false, IsWebMessageEnabled: false, AreDevToolsEnabled: false, AreDefaultContextMenusEnabled: false,
                AreBrowserAcceleratorKeysEnabled: false, IsGeneralAutofillEnabled: false, IsPasswordAutosaveEnabled: false,
                IsPinchZoomEnabled: false, AreHostObjectsAllowed: false, AreDefaultScriptDialogsEnabled: false,
            },
            "WebView2 de lecture non verrouillée");

        messages.SelectedMessage = messages.Messages.First(m => m.Uid == SmokeMailService.TrappedUid);
        await WaitForAsync(() => reader.Content is { Uid: SmokeMailService.TrappedUid } && view.LoadedPage is not null, "mail piégé affiché");
        Check(reader.Subject == "Votre facture de novembre", $"objet affiché : « {reader.Subject} »");
        Check(reader.BlockedImages >= 1 && reader.OffersImages, $"images distantes bloquées : {reader.BlockedImages}");
        Check(reader.Attachments.Count == 2 && !reader.Attachments[0].IsExecutable && reader.Attachments[1].IsExecutable, "pièces jointes");
        await WaitForAsync(() => mail.FlagChanges.Contains((SmokeMailService.TrappedUid, true, null)) && messages.Messages[0].IsRead, "mail marqué lu");
        await WaitForAsync(() => view.ServedRequests >= 2, "page et image jointe servies par l'hôte local");
        await Task.Delay(1200);
        Check(view.RefusedRequests.Count == 0, $"requêtes vers l'extérieur : {string.Join(", ", view.RefusedRequests)}");

        // Mode méfiant : contrôles DMARC en échec et lien trompeur ; « Faire confiance » n'est pas proposé.
        Check(reader is { Sender: Core.Safety.SenderStatus.Spoofed, HasSafetyNotice: true, CanTrustSender: false }, $"mode méfiant du mail piégé : {reader.Sender}");
        Check(reader.SafetyMessage.Contains("evil.example", StringComparison.Ordinal), "lien trompeur non signalé dans le bandeau");
        await CaptureAsync(hwnd, directory, "10-lecture.png", report);
        report.Add($"Lecture : mail piégé affiché, {reader.BlockedImages} image(s) distante(s) bloquée(s), {view.ServedRequests} ressources locales, aucune requête vers l'extérieur");

        // Navigation vers une autre adresse : annulée. (Demandée par l'application elle-même, WebView2 la compte comme
        // un geste de l'utilisateur : la confirmation apparaît alors, et un refus n'ouvre rien.)
        var cancelled = view.CancelledNavigations;
        var page = view.LoadedPage;
        view.TryNavigate("https://evil.example/");
        await WaitForAsync(() => view.CancelledNavigations > cancelled, "navigation annulée");
        await Task.Delay(500);
        if (dialogs.Current is not null)
        {
            dialogs.Choose(ContentDialogResult.None);
            await WaitForAsync(() => dialogs.Current is null, "confirmation refusée");
        }

        Check(launcher.Opened.Count == 0 && view.LoadedPage == page, "la navigation a quitté la page du mail");

        // Lien trompeur : la confirmation montre l'adresse réelle ; refusée, rien ne s'ouvre.
        var link = reader.OpenLinkAsync(new Uri("https://evil.example/connexion"));
        await WaitForAsync(() => dialogs.Current is not null, "confirmation avant d'ouvrir un lien");
        await Task.Delay(500);
        await CaptureAsync(hwnd, directory, "11-lien.png", report);
        dialogs.Choose(ContentDialogResult.None);
        Check(!await WithTimeout(link, "lien refusé"), "lien ouvert malgré le refus");
        Check(launcher.Opened.Count == 0, "le navigateur a été ouvert");
        report.Add("Liens : navigation forcée annulée, lien ouvert seulement après confirmation (refusée)");

        // Images distantes sur demande (aucun réseau pendant le test : demandées, puis refusées localement).
        view.OfflineImages = true;
        reader.ShowImagesCommand.Execute(null);
        await WaitForAsync(() => view.AllowedRemoteImages > 0, "images distantes demandées");
        Check(!reader.OffersImages, "bandeau des images encore affiché");
        report.Add($"Images distantes : chargées seulement après « Afficher les images » ({view.AllowedRemoteImages} demandée(s))");

        // Pièces jointes : enregistrement d'un bloc ; exécutable signalé (refusé ici), jamais ouvert.
        var saved = await WithTimeout(reader.SaveAttachmentAsync(reader.Attachments[0]), "enregistrement de la pièce jointe");
        Check(saved is not null && File.ReadAllText(saved) == "%PDF-1.7 facture de test", "pièce jointe enregistrée à l'identique");

        // Marque du web posée par le gestionnaire de pièces jointes de Windows (antivirus, SmartScreen à l'ouverture).
        var zone = File.ReadAllText(saved + ":Zone.Identifier");
        Check(zone.Contains("ZoneId=3", StringComparison.Ordinal), $"marque du web absente ou inattendue : {zone.ReplaceLineEndings(" | ")}");
        Check(services.GetRequiredService<AttachmentSafety>().LastMarkedByWindows, "marque du web écrite sans le gestionnaire de pièces jointes de Windows");
        report.Add("Pièces jointes : marque du web posée par Windows (zone Internet), antivirus et SmartScreen à l'ouverture");
        var executable = reader.SaveAttachmentAsync(reader.Attachments[1]);
        await WaitForAsync(() => dialogs.Current is not null, "avertissement avant d'enregistrer un exécutable");
        await Task.Delay(500);
        await CaptureAsync(hwnd, directory, "12-piece-jointe.png", report);
        dialogs.Choose(ContentDialogResult.None);
        Check(await WithTimeout(executable, "exécutable refusé") is null && !File.Exists(Path.Combine(directory, "mise-a-jour.exe")), "exécutable enregistré malgré le refus");
        report.Add("Pièces jointes : enregistrée à l'identique ; exécutable signalé, rien enregistré sans accord");

        // Étoile, puis non lu : la liste et le compteur suivent.
        await reader.ToggleFlagCommand.ExecuteAsync(null);
        await reader.ToggleReadCommand.ExecuteAsync(null);
        Check(messages.Messages[0] is { IsFlagged: true, IsRead: false }, "étoile et non lu dans la liste");
        Check(mail.FlagChanges.Contains((SmokeMailService.TrappedUid, null, true)) && mail.FlagChanges.Contains((SmokeMailService.TrappedUid, false, null)), "drapeaux envoyés au serveur");
        report.Add("Lu / non lu et étoile : OK");

        // Expéditeur sûr : « Faire confiance » depuis un mail ordinaire, puis retiré dans les Réglages.
        var trusted = services.GetRequiredService<TrustedSenders>();
        var ordinary = messages.Messages.First(m => m.Uid != SmokeMailService.TrappedUid);
        messages.SelectedMessage = ordinary;
        await WaitForAsync(() => reader.Content?.Uid == ordinary.Uid && !reader.IsLoading, "mail ordinaire affiché");
        Check(reader is { Sender: Core.Safety.SenderStatus.Unknown, CanTrustSender: true, CanTrustDomain: true }, $"expéditeur inconnu : {reader.Sender}");
        reader.TrustSenderCommand.Execute(null);
        Check(reader.Sender == Core.Safety.SenderStatus.Trusted && !reader.HasSafetyNotice && trusted.Addresses.Count == 1, "expéditeur non ajouté aux expéditeurs sûrs");
        var settingsDialog = window.ShowSettingsAsync();
        await WaitForAsync(() => dialogs.Current is not null, "réglages (expéditeurs sûrs)");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "22-expediteurs-surs.png", report);
        var list = services.GetRequiredService<TrustedSendersViewModel>();
        Check(list.Items.Count == 1, $"expéditeurs sûrs dans les réglages : {list.Items.Count}");
        list.Remove(list.Items[0]);
        Check(trusted.Addresses.Count == 0 && reader.Sender == Core.Safety.SenderStatus.Unknown, "expéditeur retiré mais encore sûr");
        dialogs.Current?.Hide();
        await WithTimeout(settingsDialog, "fermeture des réglages");
        report.Add("Expéditeurs sûrs : « Faire confiance » depuis la lecture, retiré dans les Réglages ; mail piégé en mode méfiant (DMARC en échec, lien trompeur)");

        // Retour au mail piégé (la suite y répond).
        messages.SelectedMessage = messages.Messages.First(m => m.Uid == SmokeMailService.TrappedUid);
        await WaitForAsync(() => reader.Content?.Uid == SmokeMailService.TrappedUid && !reader.IsLoading, "retour au mail piégé");
    }

    /// <summary>
    /// Écrire (serveur de test en mémoire, aucun réseau, rien n'est acheminé) : réponse au mail lu (destinataire, objet,
    /// citation) puis « Garder ce brouillon ? » → Supprimer, transfert avec ses pièces jointes, nouveau message avec une
    /// pièce jointe, brouillon enregistré en local et copié dans Brouillons, destinataire invalide refusé, puis envoi par
    /// le bouton Envoyer : copie dans Envoyés, brouillon effacé partout.
    /// </summary>
    private static async Task ComposeAsync(MainWindow window, IServiceProvider services, DialogService dialogs, IntPtr hwnd, string directory, List<string> report)
    {
        var composer = window.Composer;
        var reader = window.Reader.Reader;
        var mail = (SmokeMailService)services.GetRequiredService<IMailService>();
        var drafts = services.GetRequiredService<DraftStore>();
        var files = services.GetRequiredService<FileDialogs>();
        Check(reader is { Content: not null, Account: not null }, "un mail ouvert est attendu pour y répondre");
        var account = reader.Account!;

        // Réponse.
        Check(await WithTimeout(composer.ReplyAsync(reader.Content!, account, all: false), "réponse"), "réponse non préparée");
        Check(composer is { IsOpen: true, To: "Service client <contact1@example.org>", Subject: "Re: Votre facture de novembre" }, $"réponse : « {composer.To} » / « {composer.Subject} »");
        Check(composer.Body.Contains("> Votre facture de novembre.", StringComparison.Ordinal), "citation du mail d'origine");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "13-reponse.png", report);
        await DiscardAsync(composer, dialogs);

        // Transfert : les pièces jointes suivent.
        Check(await WithTimeout(composer.ForwardAsync(reader.Content!, account), "transfert"), "transfert non préparé");
        Check(composer is { Subject: "TR: Votre facture de novembre", To: "" } && composer.Attachments.Count == 2, $"transfert : « {composer.Subject} », {composer.Attachments.Count} pièce(s) jointe(s)");
        await DiscardAsync(composer, dialogs);
        Check(mail.Sent.Count == 0, "rien ne doit être envoyé sans le bouton Envoyer");
        report.Add("Réponse et transfert : préparés (destinataire, objet, citation, pièces jointes), supprimés sans rien envoyer");

        // Nouveau message, avec une pièce jointe.
        Check(await WithTimeout(composer.NewAsync(account.Id), "nouveau message"), "nouveau message non ouvert");
        composer.To = "pas-une-adresse";
        composer.Subject = "Test d'envoi de Quai Mail";
        composer.Body = "Bonjour,\nceci est un test (aucun réseau, rien n'est acheminé).";
        var file = Path.Combine(directory, "piece-jointe.txt");
        File.WriteAllText(file, "pièce jointe de test");
        files.SmokeAttachments.Add(file);
        await WithTimeout(composer.AttachCommand.ExecuteAsync(null), "pièce jointe");
        Check(composer.Attachments.Count == 1, "pièce jointe ajoutée");
        await WithTimeout(composer.AutoSaveAsync(), "enregistrement automatique");
        var draftId = composer.Draft!.Id;
        Check(drafts.Load(draftId)?.Subject == "Test d'envoi de Quai Mail", "brouillon enregistré en local");
        Check(mail.SavedDrafts.Count >= 1, "brouillon copié dans Brouillons");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "14-nouveau-message.png", report);

        await WithTimeout(composer.SendCommand.ExecuteAsync(null), "envoi refusé");
        Check(composer.HasError && composer.IsOpen && mail.Sent.Count == 0, "un destinataire invalide doit tout arrêter");

        composer.To = "Destinataire <destinataire@example.org>";
        await WithTimeout(composer.SendCommand.ExecuteAsync(null), "envoi");
        var sent = mail.Sent.SingleOrDefault() ?? throw new InvalidOperationException("Vérification en échec : aucun mail envoyé");
        Check(sent.To.Mailboxes.Single().Address == "destinataire@example.org" && sent.Attachments.Count() == 1, "mail envoyé incomplet");
        Check(!composer.IsOpen && drafts.Load(draftId) is null && mail.DeletedDrafts.Count >= 1, "brouillon non effacé après l'envoi");
        Check(composer.ShowSent, "bandeau « Mail envoyé » absent");
        await Task.Delay(600);
        await CaptureAsync(hwnd, directory, "15-envoye.png", report);
        composer.ShowSent = false;
        report.Add("Nouveau message : brouillon enregistré (local et Brouillons), destinataire invalide refusé, envoyé par le bouton Envoyer, brouillon effacé");
    }

    /// <summary>
    /// Confort : recherche (cache puis serveur), mail reçu en direct et clic sur sa notification, corbeille, suppression
    /// définitive seulement après confirmation (refusée ici), déplacement, boîte de réception unifiée avec un deuxième
    /// compte, lien mailto: (brouillon prérempli, rien n'est envoyé).
    /// </summary>
    private static async Task ComfortAsync(MainWindow window, IServiceProvider services, DialogService dialogs, Account account, IntPtr hwnd, string directory, List<string> report)
    {
        var mailbox = window.Mailbox;
        var messages = window.Messages;
        var mail = (SmokeMailService)services.GetRequiredService<IMailService>();
        var inbox = mailbox.Items.First(i => i.AccountId == account.Id && i.Role == FolderRole.Inbox);
        mailbox.SelectedItem = inbox;
        await WaitForAsync(() => !messages.IsLoading && messages.Messages.Count > 0 && messages.FolderName == "INBOX", "boîte de réception");

        // Recherche plein texte dans le cache (sans accents ni casse), puis sur le serveur.
        await WithTimeout(messages.SearchAsync("FACTURE novembre"), "recherche");
        Check(messages.IsSearching && messages.Messages.Any(m => m.Uid == SmokeMailService.TrappedUid), $"recherche : {messages.Messages.Count} résultat(s)");
        await Task.Delay(600);
        await CaptureAsync(hwnd, directory, "16-recherche.png", report);
        await WithTimeout(messages.SearchServerCommand.ExecuteAsync(null), "recherche sur le serveur");
        await WithTimeout(messages.EndSearchAsync(), "fin de la recherche");
        await WaitForAsync(() => !messages.IsLoading && !messages.IsSearching && messages.FolderName == "INBOX", "retour au dossier");
        report.Add("Recherche : trouvée dans le cache (FTS5, sans accents ni casse), puis sur le serveur");

        // Réception en direct (simulée) : le mail arrive en tête, le compteur suit ; clic sur sa notification.
        var watcher = (SmokeMailWatcher)services.GetRequiredService<IMailWatcher>();
        Check(watcher.Watched.Any(a => a.Id == account.Id), "le compte n'est pas surveillé (réception en direct)");
        var unread = inbox.Unread;
        var fresh = new MessageHeader(500, "Arrivé en direct", "Léa Dubois", "lea@example.org", DateTimeOffset.Now, false, false, 1000);
        watcher.Deliver(account, [fresh]);
        await WaitForAsync(() => messages.Messages.Count > 0 && messages.Messages[0].Uid == 500, "mail reçu en direct en tête de liste");
        Check(inbox.Unread == unread + 1, $"compteur après un mail reçu : {inbox.Unread}");
        mailbox.SelectedItem = mailbox.Items.First(i => i.Folder?.FullName == "Envoyés");
        ((SmokeNotifier)services.GetRequiredService<INotifier>()).Click(account.Id);
        await WaitForAsync(() => mailbox.SelectedItem == inbox && !messages.IsLoading, "clic sur une notification : boîte de réception");
        report.Add("Réception en direct : mail ajouté en tête, compteur mis à jour, clic sur la notification vers la boîte de réception");

        // Supprimer : vers la corbeille, sans question.
        var victim = messages.Messages.First(m => m.Uid != SmokeMailService.TrappedUid);
        Check(await WithTimeout(mailbox.DeleteAsync(victim), "suppression"), "mail non supprimé");
        Check(mail.Moves.Contains((victim.Uid, "INBOX", "Corbeille")) && !messages.Messages.Contains(victim) && dialogs.Current is null, "mail non mis à la corbeille");

        // Depuis la corbeille : suppression définitive seulement après confirmation (refusée ici).
        var trashed = new MessageItemViewModel(account.Id, "Corbeille", victim.Header, "(sans objet)", DateTimeOffset.Now);
        var forever = mailbox.DeleteAsync(trashed);
        await WaitForAsync(() => dialogs.Current is not null, "confirmation de la suppression définitive");
        await Task.Delay(500);
        await CaptureAsync(hwnd, directory, "17-supprimer-definitivement.png", report);
        dialogs.Choose(ContentDialogResult.None);
        Check(!await WithTimeout(forever, "suppression définitive refusée") && mail.PermanentlyDeleted.Count == 0, "suppression définitive sans accord");

        // Déplacer vers un autre dossier du compte.
        var moved = messages.Messages.First(m => m.Uid != SmokeMailService.TrappedUid);
        var target = mailbox.MoveTargets(moved).First(t => t.Folder?.FullName == "Archives/2026");
        Check(await WithTimeout(mailbox.MoveAsync(moved, target), "déplacement"), "mail non déplacé");
        Check(mail.Moves.Contains((moved.Uid, "INBOX", "Archives/2026")), "déplacement non demandé au serveur");
        report.Add("Supprimer : corbeille d'abord ; définitif seulement après confirmation (refusée) ; déplacer : OK");

        // Ctrl+A dans la vraie liste : tout est choisi et rien de neuf ne s'ouvre (donc rien n'est marqué lu) ; le clic
        // droit vise toute la sélection : « Marquer comme lu » en une commande, puis non lu pour ceux qui l'étaient.
        var reader = mailbox.Reader;
        uint? ShownUid() => reader.HasMessage ? reader.Uid : null;
        var shownUid = ShownUid();
        var flags = mail.FlagChanges.Count;
        window.SelectAllMessages();
        await WaitForAsync(() => messages.SelectedMessages.Count == messages.Messages.Count, "Ctrl+A : tous les mails choisis");
        Check(ShownUid() == shownUid && mail.FlagChanges.Count == flags, "Ctrl+A a ouvert (et marqué lu) un mail");
        var all = messages.TargetsOf(messages.Messages[^1]);
        Check(all.Count == messages.Messages.Count, $"clic droit sur la sélection : {all.Count} mail(s) visé(s) sur {messages.Messages.Count}");
        var wereUnread = all.Where(m => !m.IsRead).ToList();
        Check(wereUnread.Count > 0, "aucun mail non lu pour essayer « Marquer comme lu »");
        var inboxUnread = inbox.Unread;
        await Task.Delay(500);
        await CaptureAsync(hwnd, directory, "17-tout-selectionner.png", report);
        await WithTimeout(window.SetReadAsync(all, read: true), "sélection marquée lue");
        Check(messages.Messages.All(m => m.IsRead) && mail.Batches[^1] == $"flags INBOX {wereUnread.Count}", $"sélection non marquée lue en une commande ({mail.Batches[^1]})");
        Check(inbox.Unread == Math.Max(0, inboxUnread - wereUnread.Count), $"compteur après « Marquer comme lu » : {inbox.Unread}");
        await WithTimeout(window.SetReadAsync(wereUnread, read: false), "mails remis non lus");
        Check(wereUnread.All(m => !m.IsRead), "mails non remis non lus");

        // Maj + clic sur trois mails, puis Suppr : les trois à la corbeille en une commande, sans question, et aucun
        // ne reste choisi ni ouvert.
        window.SelectMessageRange(messages.Messages.Count - 4, 3);
        await WaitForAsync(() => messages.SelectedMessages.Count == 3, "Maj + clic : trois mails choisis");
        var three = messages.SelectedMessages.ToList();
        await WithTimeout(window.DeleteAsync(messages.SelectedMessages), "suppression de la sélection");
        Check(
            three.All(m => !messages.Messages.Contains(m) && mail.Moves.Contains((m.Uid, "INBOX", "Corbeille"))) && mail.Batches[^1] == "trash INBOX 3" && dialogs.Current is null,
            $"sélection non mise à la corbeille en une commande ({mail.Batches[^1]})");
        Check(messages.SelectedMessages.Count == 0 && !three.Any(m => ShownUid() == m.Uid), "un mail supprimé est resté choisi ou ouvert");
        report.Add("Sélection : Ctrl+A choisit tout sans rien ouvrir ; lu / non lu et corbeille en une commande par dossier");

        // Boîte de réception unifiée : un deuxième compte (même serveur de test).
        var accounts = services.GetRequiredService<AccountStore>();
        var credentials = services.GetRequiredService<ICredentialStore>();
        var second = account with { Id = Guid.NewGuid().ToString("N"), Address = "dominique@example.org", DisplayName = "Dominique", UserName = "dominique@example.org" };
        credentials.SetPassword(second.Id, SmokeMailService.Password);
        accounts.Save(second);
        await WithTimeout(mailbox.RefreshAsync(), "deuxième compte");
        var unified = mailbox.Items.FirstOrDefault(i => i.IsUnified) ?? throw new InvalidOperationException("Vérification en échec : boîte de réception unifiée absente");
        mailbox.SelectedItem = unified;
        await WaitForAsync(() => !messages.IsLoading && messages.IsUnified && messages.Messages.Select(m => m.AccountId).Distinct().Count() == 2, "boîte de réception unifiée");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "18-boites-unifiees.png", report);

        // Replier un compte : ses dossiers quittent le volet, le dossier affiché reste affiché (sans surbrillance) et le
        // choix est gardé dans les réglages ; déplié, ses dossiers reviennent et la surbrillance aussi.
        var settings = services.GetRequiredService<ISettingsService>();
        var sentFolder = mailbox.Items.First(i => i.AccountId == account.Id && i.Folder?.FullName == "Envoyés");
        mailbox.SelectedItem = sentFolder;
        await WaitForAsync(() => !messages.IsLoading && messages.FolderName == "Envoyés" && window.NavSelection == sentFolder, "dossier Envoyés en surbrillance");
        window.SetAccountExpanded(account.Id, false);
        await WaitForAsync(() => window.NavSelection is null, "surbrillance retirée du dossier masqué");
        Check(!mailbox.Items.Any(i => i.AccountId == account.Id && !i.IsAccount) && mailbox.Items.Any(i => i.AccountId == second.Id && !i.IsAccount), "compte replié : ses dossiers seuls doivent quitter le volet");
        Check(mailbox.SelectedItem == sentFolder && messages.FolderName == "Envoyés", "compte replié : le dossier affiché doit rester affiché");
        Check(settings.Current.CollapsedAccounts.Contains(account.Id), "compte replié non gardé dans les réglages");
        await Task.Delay(500);
        await CaptureAsync(hwnd, directory, "18-compte-replie.png", report);
        window.SetAccountExpanded(account.Id, true);
        await WaitForAsync(() => window.NavSelection == sentFolder, "surbrillance rendue au dossier affiché");
        Check(settings.Current.CollapsedAccounts.Length == 0, "compte déplié encore noté replié");
        report.Add("Volet : un compte se replie et se déplie (dossier affiché gardé), choix gardé dans les réglages");

        var removing = mailbox.RemoveAccountAsync(second.Id);
        await WaitForAsync(() => dialogs.Current is not null, "retrait du deuxième compte");
        dialogs.Choose(ContentDialogResult.Primary);
        Check(await WithTimeout(removing, "retrait du deuxième compte"), "deuxième compte non retiré");
        report.Add("Boîte de réception unifiée : deux comptes mêlés par date");

        // Lien mailto: : brouillon prérempli, rien n'est envoyé.
        var composer = window.Composer;
        var sent = mail.Sent.Count;
        Check(Core.Compose.MailtoLink.TryParse("mailto:lea@example.org?subject=Depuis%20un%20lien&body=Bonjour", out var link), "lien mailto illisible");
        Check(await WithTimeout(composer.NewFromLinkAsync(link, account.Id), "lien mailto"), "lien mailto non ouvert");
        Check(composer is { IsOpen: true, To: "lea@example.org", Subject: "Depuis un lien", Body: "Bonjour" }, "brouillon du lien mailto");
        await DiscardAsync(composer, dialogs);
        Check(mail.Sent.Count == sent, "un lien mailto ne doit rien envoyer");
        report.Add("Lien mailto : brouillon prérempli, rien envoyé");
    }

    /// <summary>
    /// Connexion par le navigateur (fournisseur simulé : le « navigateur » suit aussitôt le retour local, jetons en
    /// mémoire, aucune requête vers l'extérieur) : bouton « Se connecter avec Google » du formulaire, PKCE, jeton de
    /// renouvellement dans le coffre de Windows et nulle part ailleurs, « Se reconnecter », retrait du compte.
    /// </summary>
    private static async Task BrowserSignInAsync(MainWindow window, IServiceProvider services, DialogService dialogs, IntPtr hwnd, string directory, List<string> report)
    {
        var embedded = BrowserSignIn.Embedded;
        report.Add($"Inscription OAuth intégrée à cette compilation : Google {(embedded.For(AccountAuth.Google) is null ? "non" : "oui")}, Microsoft {(embedded.For(AccountAuth.Microsoft) is null ? "non" : "oui")}");

        var mailbox = window.Mailbox;
        var browser = services.GetRequiredService<SmokeBrowser>();
        var credentials = services.GetRequiredService<ICredentialStore>();
        var paths = services.GetRequiredService<AppPaths>();
        var adding = window.AddAccountAsync();
        await WaitForAsync(() => dialogs.Current is AccountDialog, "formulaire d'ajout de compte (navigateur)");
        var dialog = (AccountDialog)dialogs.Current!;
        var editor = dialog.Editor;
        if (!BrowserSignIn.Available)
        {
            // Pas encore proposée : boutons grisés « à venir », l'ajout par mot de passe reste la voie normale.
            Check(!editor.CanSignIn, "connexion par le navigateur proposée alors qu'elle est à venir");
            await WaitForAsync(() => dialog.IsLoaded && !dialog.SignInButtonsEnabled, "boutons de connexion par le navigateur grisés");
            Check(editor.GoogleSignInText == Loc.Get("AccountDialog_SignInGoogleSoon") && editor.MicrosoftSignInText == Loc.Get("AccountDialog_SignInMicrosoftSoon"), "mention « à venir » absente");
            editor.Address = "dominique@outlook.fr";
            await WithTimeout(editor.DetectAsync(), "détection Outlook.com");
            await Task.Delay(800);
            await CaptureAsync(hwnd, directory, "19-connexion-a-venir.png", report);
            dialogs.Current?.Hide();
            Check(await WithTimeout(adding, "fermeture du formulaire") is null && mailbox.HasNoAccount, "aucun compte ne doit être ajouté");
            Check(browser.Opened.Count == 0, "aucune page de connexion ne doit s'ouvrir");
            report.Add("Connexion Gmail / Outlook.com par le navigateur : boutons grisés « à venir »");
            return;
        }

        Check(editor is { CanSignInWithGoogle: true, CanSignInWithMicrosoft: true }, "boutons « Se connecter avec Google / Microsoft » absents");
        editor.Address = "dominique@gmail.com";
        await WithTimeout(editor.DetectAsync(), "détection Gmail");
        Check(editor.Hint?.Contains(Loc.Get("AccountDialog_SignInGoogle"), StringComparison.Ordinal) == true, $"conseil Gmail : « {editor.Hint} »");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "19-connexion-navigateur.png", report);

        dialog.ClickSignInWithGoogle();
        var account = await WithTimeout(adding, "connexion Google") ?? throw new InvalidOperationException($"Vérification en échec : compte Google non ajouté ({editor.ErrorMessage})");
        Check(account.Auth == AccountAuth.Google, "compte enregistré sans la connexion Google");
        var opened = browser.Opened.Single();
        var query = System.Web.HttpUtility.ParseQueryString(opened.Query);
        Check(
            opened.GetLeftPart(UriPartial.Path) == "https://accounts.google.com/o/oauth2/v2/auth" && query["code_challenge_method"] == "S256"
            && query["redirect_uri"]?.StartsWith("http://", StringComparison.Ordinal) == true && new Uri(query["redirect_uri"]!).IsLoopback,
            $"page de connexion : {opened.GetLeftPart(UriPartial.Path)}");
        Check(credentials.GetPassword(account.Id) == SmokeTokenEndpoint.RefreshToken, "jeton de renouvellement absent du coffre de Windows");
        var json = File.ReadAllText(paths.AccountsFile);
        Check(
            !json.Contains(SmokeTokenEndpoint.RefreshToken, StringComparison.Ordinal) && !json.Contains(SmokeMailService.AccessToken, StringComparison.Ordinal)
            && json.Contains("\"auth\": \"google\"", StringComparison.Ordinal),
            "accounts.json : jeton écrit ou connexion Google absente");
        await WaitForAsync(() => mailbox.Items.Any(i => i.IsAccount && i.AccountId == account.Id && i.UsesBrowserSignIn), "compte Google affiché");
        report.Add("Connexion par le navigateur : page officielle (PKCE), retour local, jeton de renouvellement dans le coffre de Windows seulement");

        Check(await WithTimeout(window.ReconnectAccountAsync(account.Id), "se reconnecter"), $"reconnexion refusée : {mailbox.ErrorMessage}");
        Check(browser.Opened.Count == 2 && System.Web.HttpUtility.ParseQueryString(browser.Opened[1].Query)["login_hint"] == "dominique@gmail.com", "« Se reconnecter » n'a pas rouvert la page de connexion");
        report.Add("Se reconnecter : nouvelle page de connexion pour le même compte");

        var removing = mailbox.RemoveAccountAsync(account.Id);
        await WaitForAsync(() => dialogs.Current is not null, "retrait du compte Google");
        dialogs.Choose(ContentDialogResult.Primary);
        Check(await WithTimeout(removing, "retrait du compte Google"), "compte Google non retiré");
        Check(credentials.GetPassword(account.Id) is null, "jeton de renouvellement resté dans le coffre après le retrait");
        report.Add("Retrait du compte Google : jeton de renouvellement effacé du coffre");
    }

    /// <summary>
    /// Signature OpenPGP : clé créée pour le test, importée dans les Réglages, bouton Signer actif pour l'adresse du
    /// compte, phrase de passe refusée puis acceptée, mail parti signé (PGP/MIME) et vérifié avec la clé publique seule ;
    /// phrase gardée pour la session, puis la clé retirée.
    /// </summary>
    private static async Task SignedMailAsync(MainWindow window, IServiceProvider services, DialogService dialogs, Account account, IntPtr hwnd, string directory, List<string> report)
    {
        var composer = window.Composer;
        var mail = (SmokeMailService)services.GetRequiredService<IMailService>();
        var files = services.GetRequiredService<FileDialogs>();
        var keys = services.GetRequiredService<PgpKeysViewModel>();
        var store = services.GetRequiredService<Core.Pgp.PgpKeyStore>();
        var keyFile = Path.Combine(directory, "cle-secrete-de-test.asc");
        var ring = SmokePgp.CreateKey($"Camille <{account.Address}>", keyFile);

        // Réglages : Kleopatra « détecté » (GnuPG du test, jamais celui de l'ordinateur), sa clé listée puis copiée dans
        // Quai Mail avec sa phrase de passe (refusée, puis acceptée).
        var gnupg = (SmokeGnuPgLocator)services.GetRequiredService<Core.Pgp.IGnuPgLocator>();
        gnupg.Key = (Convert.ToHexString(ring.GetPublicKey().GetFingerprint()), $"Camille <{account.Address}>", File.ReadAllText(keyFile));
        var settings = window.ShowSettingsAsync();
        await WaitForAsync(() => dialogs.Current is SettingsDialog, "réglages (clé OpenPGP)");
        await WaitForAsync(() => keys.HasGnuPg && keys.GnuPgKeys.Count == 1, "Kleopatra détecté dans les Réglages");
        var found = keys.GnuPgKeys[0];
        Check(found.CanImport && !keys.HasKeys, $"clé de Kleopatra non proposée : {found.Status}");
        Check(
            !await WithTimeout(keys.ImportFromGnuPgAsync(found, "pas la bonne"), "phrase de Kleopatra refusée") && keys.IsError && !keys.HasKeys,
            "clé copiée malgré une phrase de passe refusée");
        Check(await WithTimeout(keys.ImportFromGnuPgAsync(found, SmokePgp.Passphrase), "import depuis Kleopatra"), $"clé de Kleopatra non importée : {keys.Message}");
        Check(keys is { HasKeys: true, IsError: false } && keys.Keys.Count == 1 && !keys.GnuPgKeys[0].CanImport, "clé copiée : la ligne de Kleopatra propose encore l'import");
        Check(gnupg.Exports == 2, $"exports demandés à GnuPG : {gnupg.Exports}");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "20-cle-kleopatra.png", report);
        report.Add("Kleopatra détecté : clé listée dans les Réglages, copiée dans Quai Mail avec sa phrase de passe (refusée, puis acceptée)");

        // Sans Kleopatra : fichier exporté (la même clé, remplacée).
        files.SmokeKeyFile = keyFile;
        await WithTimeout(keys.ImportAsync(), "import de la clé OpenPGP");
        Check(keys is { HasKeys: true, IsError: false } && keys.Keys.Count == 1, $"clé OpenPGP non importée : {keys.Message}");
        Check(keys.Keys[0].Key.Addresses.Contains(account.Address), "adresse de la clé");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "20-cle-openpgp.png", report);
        dialogs.Current?.Hide();
        await WithTimeout(settings, "fermeture des réglages");
        foreach (var file in Directory.GetFiles(services.GetRequiredService<AppPaths>().PgpDirectory))
        {
            Check(!File.ReadAllText(file).Contains(SmokePgp.Passphrase, StringComparison.Ordinal), "phrase de passe écrite sur disque");
        }

        // Nouveau message : Signer actif d'office ; phrase refusée, puis acceptée.
        var sent = mail.Sent.Count;
        Check(await WithTimeout(composer.NewAsync(account.Id), "message signé"), "nouveau message non ouvert");
        Check(composer is { CanSign: true, Sign: true }, "bouton Signer inactif malgré la clé");
        composer.To = "Destinataire <destinataire@example.org>";
        composer.Subject = "Message signé";
        composer.Body = "Signé avec OpenPGP par le test de fumée.";
        var sending = composer.SendCommand.ExecuteAsync(null);
        await WaitForAsync(() => dialogs.Current is PassphraseDialog, "phrase de passe demandée");
        var first = (PassphraseDialog)dialogs.Current!;
        first.Passphrase = "pas la bonne";
        dialogs.Choose(ContentDialogResult.Primary);
        await WaitForAsync(() => dialogs.Current is PassphraseDialog { Retry: true } again && again != first, "phrase de passe redemandée");
        Check(mail.Sent.Count == sent, "rien ne doit partir avec une phrase de passe refusée");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "21-phrase-de-passe.png", report);
        ((PassphraseDialog)dialogs.Current!).Passphrase = SmokePgp.Passphrase;
        dialogs.Choose(ContentDialogResult.Primary);
        await WithTimeout(sending, "envoi signé");
        Check(mail.Sent.Count == sent + 1 && !composer.IsOpen, $"mail signé non envoyé : {composer.ErrorMessage}");
        Check(SmokePgp.Verify(mail.Sent[^1], ring, Path.Combine(directory, "destinataire-pgp")), "signature OpenPGP absente ou invalide");
        composer.ShowSent = false;
        report.Add("Signature OpenPGP : clé importée, phrase refusée puis acceptée, mail parti en PGP/MIME, signature vérifiée avec la clé publique");

        // Pendant la session, plus de question ; décoché, le mail part sans signature.
        Check(await WithTimeout(composer.NewAsync(account.Id), "deuxième message signé"), "nouveau message non ouvert");
        composer.To = "destinataire@example.org";
        composer.Subject = "Deuxième";
        composer.Body = "Toujours signé.";
        await WithTimeout(composer.SendCommand.ExecuteAsync(null), "deuxième envoi signé");
        Check(SmokePgp.Verify(mail.Sent[^1], ring, Path.Combine(directory, "destinataire-pgp")), "deuxième mail non signé");
        Check(await WithTimeout(composer.NewAsync(account.Id), "message non signé"), "nouveau message non ouvert");
        composer.Sign = false;
        composer.To = "destinataire@example.org";
        composer.Subject = "Sans signature";
        composer.Body = "Pas signé.";
        await WithTimeout(composer.SendCommand.ExecuteAsync(null), "envoi sans signature");
        Check(mail.Sent[^1].Body is not MimeKit.Cryptography.MultipartSigned, "mail signé alors que Signer était décoché");
        composer.ShowSent = false;

        // Clé retirée (Réglages rouverts : la liste suit, l'ancien dialogue fermé ne gêne pas) : Signer n'est plus proposé.
        settings = window.ShowSettingsAsync();
        await WaitForAsync(() => dialogs.Current is SettingsDialog, "réglages (retrait de la clé)");
        keys.Remove(keys.Keys[0].Key);
        Check(!keys.HasKeys, "clé encore listée");
        dialogs.Current?.Hide();
        await WithTimeout(settings, "fermeture des réglages");
        Check(store.Keys.Count == 0, "clé OpenPGP non retirée");
        Check(await WithTimeout(composer.NewAsync(account.Id), "message sans clé"), "nouveau message non ouvert");
        Check(composer is { CanSign: false, Sign: false }, "Signer encore proposé sans clé");
        composer.Body = "Brouillon sans clé";
        await DiscardAsync(composer, dialogs);
        report.Add("Signature OpenPGP : phrase gardée pour la session, Signer décoché respecté, clé retirée");
    }

    /// <summary>Ferme le message en cours : « Garder ce brouillon ? » → Supprimer.</summary>
    private static async Task DiscardAsync(ComposerViewModel composer, DialogService dialogs)
    {
        var closing = composer.CloseCommand.ExecuteAsync(null);
        await WaitForAsync(() => dialogs.Current is not null, "« Garder ce brouillon ? »");
        dialogs.Choose(ContentDialogResult.Secondary);
        await WithTimeout(closing, "brouillon supprimé");
        Check(!composer.IsOpen, "message encore ouvert après « Supprimer »");
    }

    /// <summary>Bandeau de mise à jour : une release plus récente (fictive, sans réseau) l'affiche.</summary>
    private static async Task UpdateBannerAsync(MainWindow window, IServiceProvider services, IntPtr hwnd, string directory, List<string> report)
    {
        var banner = services.GetRequiredService<UpdateBannerViewModel>();
        await banner.CheckAsync();
        Check(
            banner.IsOpen && banner.ReleasePage?.AbsoluteUri == $"https://github.com/memton80/quai-mail/releases/tag/{ReleaseTag}",
            "le bandeau de mise à jour ne s'affiche pas pour une version plus récente");
        Check(!banner.CanInstallInPlace, "release fictive sans installateur : le bouton doit ouvrir la page de la release");
        await Task.Delay(800);
        await CaptureAsync(hwnd, directory, "6-mise-a-jour.png", report);
        banner.IsOpen = false;
        await Task.Delay(300);
        Check(window.Updates.IsOpen == false, "fermeture du bandeau de mise à jour");
        report.Add("Bandeau de mise à jour : OK");
    }

    public static void Fail(string directory, Exception? exception)
    {
        TryWrite(Path.Combine(directory, "smoke-error.txt"), exception?.ToString() ?? "Erreur inconnue");
        Environment.Exit(1);
    }

    private static async Task CaptureAsync(IntPtr hwnd, string directory, string name, List<string> report)
    {
        var colors = await ScreenCapture.SaveAsync(hwnd, Path.Combine(directory, name));
        report.Add($"Capture {name} : {colors} couleurs");
        Check(colors > 20, $"capture {name} presque uniforme ({colors} couleurs) : fenêtre vide ?");
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string step)
    {
        await WithTimeout((Task)task, step);
        return await task;
    }

    private static async Task WithTimeout(Task task, string step)
    {
        if (await Task.WhenAny(task, Task.Delay(StepTimeout)) != task)
        {
            throw new TimeoutException($"Étape « {step} » sans réponse après {StepTimeout.TotalSeconds} s.");
        }

        await task;
    }

    private static async Task WaitForAsync(Func<bool> condition, string step)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Étape « {step} » non atteinte après {StepTimeout.TotalSeconds} s.");
            }

            await Task.Delay(100);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Vérification en échec : " + what);
        }
    }

    private static void TryWrite(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch (Exception)
        {
            // Rien de plus à faire : le code de sortie signale déjà l'échec.
        }
    }
}

/// <summary>Source de releases du test de fumée : une version plus récente, sans fichier, sans requête réseau.</summary>
internal sealed class SmokeReleaseSource : IReleaseSource
{
    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ReleaseInfo?>(new ReleaseInfo(SmokeTest.ReleaseTag, []));
}
