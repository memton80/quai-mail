using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using QuaiMail.App.Services;
using QuaiMail.Core;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App;

/// <summary>Point d'entrée de l'application : conteneur de services, fenêtre principale, erreurs non gérées.</summary>
public partial class App : Application
{
    private readonly ILogger<App> _logger;
    private readonly string? _smokeTestDirectory;
    private readonly string? _updateTestDirectory;

    // Passé par l'installateur quand il relance Quai Mail après une mise à jour.
    private const string AfterUpdateSwitch = "--after-update";

    public App()
    {
        // Désinstallation avec effacement des données : mots de passe retirés du coffre de Windows, sans fenêtre.
        if (SecretPurge.IsRequested())
        {
            Environment.Exit(SecretPurge.Run());
        }

        if (SmokeTest.TryGetOutputDirectory(out var smokeDirectory))
        {
            _smokeTestDirectory = smokeDirectory;
            SmokeTest.IsolateData(smokeDirectory);
        }

        string? updateTestInstaller = null;
        if (SmokeTest.TryGetUpdateTest(out var updateDirectory, out var installer))
        {
            _updateTestDirectory = updateDirectory;
            updateTestInstaller = installer;
            SmokeTest.IsolateData(updateDirectory);
        }

        Services = AppServices.Build(smokeTest: _smokeTestDirectory is not null, updateTestInstaller);
        _logger = Services.GetRequiredService<ILogger<App>>();

        // Interface en français d'abord : force aussi les textes fournis par WinUI (boutons, menus).
        TrySetLanguage("fr-FR");

        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger.LogError(e.Exception, "Erreur non observée dans une tâche");
            e.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services { get; }

    public MainWindow? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.LogInformation("Démarrage de Quai Mail {Version}", AppInfo.Version);
        var afterUpdate = Environment.GetCommandLineArgs().Contains(AfterUpdateSwitch, StringComparer.OrdinalIgnoreCase);
        if (afterUpdate)
        {
            // Relancé par l'installateur au bout du bouton « Mettre à jour » (installer/QuaiMail.iss).
            _logger.LogInformation("Démarrage juste après une mise à jour (relancé par l'installateur)");
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();

        if (afterUpdate && _smokeTestDirectory is null && _updateTestDirectory is null)
        {
            // Compte, dossier, mail ouvert et brouillon notés juste avant la mise à jour (UpdateInstaller), une seule fois.
            RestoreAfterUpdate();
        }

        // Comptes et dossiers : le cache tout de suite, puis les serveurs (après la restauration, qui choisit le dossier).
        _ = LoadMailboxAsync();

        if (_smokeTestDirectory is not null)
        {
            _ = SmokeTest.RunAsync(MainWindow, Services, _logger, _smokeTestDirectory);
            return;
        }

        if (_updateTestDirectory is not null)
        {
            _ = SmokeTest.RunUpdateAsync(MainWindow, Services, _logger, _updateTestDirectory);
            return;
        }

        _ = CheckForUpdatesAsync();
    }

    private void RestoreAfterUpdate()
    {
        try
        {
            Services.GetRequiredService<ShellViewModel>().RestoreAfterUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "État de la fenêtre non restauré après la mise à jour");
        }
    }

    private async Task LoadMailboxAsync()
    {
        try
        {
            await Services.GetRequiredService<MailboxViewModel>().LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chargement des comptes impossible");
        }
    }

    /// <summary>Vérification des mises à jour, un peu après le démarrage pour ne pas le ralentir.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Services.GetRequiredService<UpdateBannerViewModel>().CheckAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vérification des mises à jour interrompue");
        }
    }

    private void TrySetLanguage(string language)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de forcer la langue {Language}", language);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Erreur non gérée");

        if ((_smokeTestDirectory ?? _updateTestDirectory) is { } testDirectory)
        {
            SmokeTest.Fail(testDirectory, e.Exception);
            return;
        }

        // Une erreur inattendue dans un gestionnaire d'interface ne doit pas fermer l'application
        // et faire perdre le brouillon en cours : elle est journalisée et l'exécution continue.
        e.Handled = true;
    }
}
