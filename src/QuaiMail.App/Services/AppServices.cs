using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuaiMail.Core;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Accounts.OAuth;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Updates;
using QuaiMail.Core.ViewModels;
using Serilog;

namespace QuaiMail.App.Services;

/// <summary>Composition des services de l'application (DI + journalisation fichier).</summary>
internal static class AppServices
{
    /// <param name="smokeTest">Test de fumée : la release « plus récente » est fictive, aucune requête réseau.</param>
    /// <param name="updateTestInstaller">Test de la mise à jour (CI) : cet installateur local est présenté comme la dernière release.</param>
    public static ServiceProvider Build(bool smokeTest = false, string? updateTestInstaller = null)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        // Journal local uniquement (pas de télémétrie). Ni mot de passe, ni objet, ni corps, ni adresse.
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "quaimail-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder => builder.AddSerilog(serilog, dispose: true));

        // Réglages, brouillons locaux, état de la fenêtre pour les mises à jour
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton(provider => new DraftStore(paths, provider.GetService<ILogger<DraftStore>>()));
        services.AddSingleton(provider => new WindowStateStore(paths, provider.GetService<ILogger<WindowStateStore>>()));

        // Interface
        services.AddSingleton<IStrings, ReswStrings>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(provider => provider.GetRequiredService<DialogService>());
        services.AddSingleton<IClipboard, ClipboardService>();
        services.AddSingleton<FileDialogs>();
        services.AddSingleton<IFileDialogs>(provider => provider.GetRequiredService<FileDialogs>());

        // Comptes et mail. Tests de fumée : données de test en mémoire, ressource du coffre à part, aucun réseau.
        var testing = smokeTest || updateTestInstaller is not null;
        services.AddSingleton(provider => new AccountStore(paths, provider.GetService<ILogger<AccountStore>>()));
        services.AddSingleton<ICredentialStore>(new PasswordVaultCredentialStore(testing ? PasswordVaultCredentialStore.SmokeResource : PasswordVaultCredentialStore.DefaultResource));
        services.AddSingleton(provider => new MailCache(paths.CacheDatabase, provider.GetService<ILogger<MailCache>>()));
        services.AddSingleton(_ => new ProviderAutoconfig(testing ? new NoNetworkHandler() : null));
        services.AddSingleton<MailSettingsDiscovery>();

        // Connexion par le navigateur (Gmail, Outlook.com) : identifiants intégrés à la compilation. Tests de fumée :
        // fournisseur simulé (navigateur qui suit le retour local, jetons en mémoire), aucune requête vers l'extérieur.
        services.AddSingleton(testing ? BrowserSignIn.Smoke : BrowserSignIn.Embedded);
        if (testing)
        {
            services.AddSingleton<SmokeBrowser>();
            services.AddSingleton<SmokeTokenEndpoint>();
            services.AddSingleton(provider => new OAuthFlow(
                provider.GetRequiredService<SmokeBrowser>(), provider.GetRequiredService<SmokeTokenEndpoint>(), provider.GetService<ILogger<OAuthFlow>>()));
        }
        else
        {
            services.AddSingleton(provider => new OAuthFlow(provider.GetRequiredService<ILauncher>(), null, provider.GetService<ILogger<OAuthFlow>>()));
        }

        services.AddSingleton(provider => new AccessTokens(
            provider.GetRequiredService<ICredentialStore>(), provider.GetRequiredService<OAuthFlow>(), provider.GetRequiredService<OAuthClients>()));
        if (testing)
        {
            services.AddSingleton<IMailService, SmokeMailService>();
            services.AddSingleton<IMailWatcher, SmokeMailWatcher>();
            services.AddSingleton<INotifier, SmokeNotifier>();
        }
        else
        {
            services.AddSingleton<IMailService>(provider =>
                new MailKitMailService(provider.GetRequiredService<ICredentialStore>(), provider.GetService<ILogger<MailKitMailService>>(), provider.GetRequiredService<AccessTokens>()));
            services.AddSingleton<IMailWatcher>(provider =>
                new MailKitMailWatcher(provider.GetRequiredService<ICredentialStore>(), provider.GetService<ILogger<MailKitMailWatcher>>(), provider.GetRequiredService<AccessTokens>()));
            services.AddSingleton<AppNotifier>();
            services.AddSingleton<INotifier>(provider => provider.GetRequiredService<AppNotifier>());
        }

        // View-models
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(provider => new MessageListViewModel(
            provider.GetRequiredService<IMailService>(),
            provider.GetRequiredService<MailCache>(),
            provider.GetRequiredService<IStrings>(),
            TimeProvider.System,
            provider.GetService<ILogger<MessageListViewModel>>()));
        services.AddSingleton<MailboxViewModel>();

        // Lecture : gabarit Assets/Mail/reader.html (lu une fois), liens ouverts dans le navigateur après confirmation.
        services.AddSingleton(new ReaderTemplate(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Mail", "reader.html"))));
        if (testing)
        {
            services.AddSingleton<ILauncher, SmokeLauncher>();
        }
        else
        {
            services.AddSingleton<ILauncher, Launcher>();
        }

        services.AddSingleton(provider => new ReaderViewModel(
            provider.GetRequiredService<IMailService>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IDialogService>(),
            provider.GetRequiredService<IFileDialogs>(),
            provider.GetRequiredService<ILauncher>(),
            provider.GetRequiredService<IStrings>(),
            provider.GetRequiredService<ReaderTemplate>(),
            provider.GetService<ILogger<ReaderViewModel>>(),
            provider.GetRequiredService<MailCache>()));
        services.AddSingleton(provider => new ComposerViewModel(
            provider.GetRequiredService<AccountStore>(),
            provider.GetRequiredService<DraftStore>(),
            provider.GetRequiredService<IMailService>(),
            provider.GetRequiredService<ShellViewModel>(),
            provider.GetRequiredService<IDialogService>(),
            provider.GetRequiredService<IFileDialogs>(),
            provider.GetRequiredService<IStrings>(),
            TimeProvider.System,
            provider.GetService<ILogger<ComposerViewModel>>()));
        services.AddTransient<AccountEditorViewModel>();

        // Mises à jour (comme Quai) : une requête à l'API publique de GitHub, puis, sur demande, le téléchargement de
        // l'installateur (les tests de fumée n'accèdent pas au réseau).
        var installerName = UpdateChecker.InstallerNameFor(RuntimeInformation.ProcessArchitecture);
        if (updateTestInstaller is not null)
        {
            var (source, downloader) = SmokeUpdate.Create(updateTestInstaller, installerName);
            services.AddSingleton(source);
            services.AddSingleton(downloader);
        }
        else
        {
            services.AddSingleton<IReleaseSource>(smokeTest ? new SmokeReleaseSource() : new GitHubReleaseSource());
            services.AddSingleton(_ => new UpdateDownloader());
        }

        services.AddSingleton(provider => new UpdateChecker(provider.GetRequiredService<IReleaseSource>(), AppInfo.Version, installerName));
        services.AddSingleton<IUpdateInstaller, UpdateInstaller>();
        services.AddSingleton<UpdateBannerViewModel>();

        return services.BuildServiceProvider();
    }
}
