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
