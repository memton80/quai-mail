using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using QuaiMail.App.Services;
using QuaiMail.Core;
using QuaiMail.Core.ViewModels;
using Windows.Graphics;

namespace QuaiMail.App;

/// <summary>Fenêtre principale : barre de titre, bandeau de mise à jour, trois volets (comptes et dossiers, liste, lecture).</summary>
public sealed partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly DialogService _dialogs;
    private readonly SettingsViewModel _settings;

    // Fermeture : question en cours, puis fermeture accordée (brouillon gardé ou supprimé).
    private bool _closeRequested;
    private bool _closeAllowed;

    public MainWindow()
    {
        var services = App.Current.Services;
        Shell = services.GetRequiredService<ShellViewModel>();
        Updates = services.GetRequiredService<UpdateBannerViewModel>();
        _settings = services.GetRequiredService<SettingsViewModel>();
        _dialogs = services.GetRequiredService<DialogService>();
        _logger = services.GetRequiredService<ILogger<MainWindow>>();
        InitializeComponent();

        Title = AppInfo.DisplayName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "QuaiMail.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        // Les boîtes de dialogue ont besoin de la fenêtre.
        _dialogs.Root = RootGrid;
        services.GetRequiredService<ThemeService>().Attach(this, RootGrid);

        AppWindow.Closing += OnClosing;
        ResizeForDpi(1240, 820);
    }

    /// <summary>Compte, dossier, mail ouvert, brouillon en cours.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>Bandeau « nouvelle version disponible ».</summary>
    public UpdateBannerViewModel Updates { get; }

    /// <summary>Contenu racine de la fenêtre (test de fumée).</summary>
    public FrameworkElement Root => RootGrid;

    /// <summary>Ouvre les réglages (bouton du volet de gauche, test de fumée).</summary>
    internal Task ShowSettingsAsync() => _dialogs.ShowSettingsAsync(_settings);

    /// <summary>Fermeture de la fenêtre (bouton, Alt+F4) : propose de garder le brouillon en cours.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAllowed || !Shell.HasDraft)
        {
            return;
        }

        args.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        try
        {
            if (!await Shell.ConfirmCloseDraftAsync())
            {
                return;
            }
        }
        catch (Exception ex)
        {
            // Quitter reste possible même si la question n'a pas pu être posée.
            _logger.LogError(ex, "Question avant fermeture impossible");
        }
        finally
        {
            _closeRequested = false;
        }

        _closeAllowed = true;
        Close();
    }

    /// <summary>
    /// Fermeture pour installer une mise à jour : le brouillon a déjà été proposé à « Garder » et l'état de la fenêtre
    /// noté ; l'installateur attend que Quai Mail se termine puis le relance.
    /// </summary>
    internal void CloseForUpdate()
    {
        _closeAllowed = true;
        Close();
    }

    /// <summary>Bouton du bandeau : mettre à jour (ou ouvrir la page de la release), ou annuler le téléchargement.</summary>
    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Updates.ActAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mise à jour interrompue");
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowSettingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Réglages impossibles à afficher");
        }
    }

    private void ResizeForDpi(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }
}
