using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Storage;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Views;

/// <summary>
/// Volet de lecture. Le corps du mail est affiché par une WebView2 <b>verrouillée</b> (CLAUDE.md) : JavaScript coupé,
/// pas de messages web, ni menus, ni outils de développement, ni raccourcis du navigateur, ni zoom par pincement, ni
/// remplissage automatique, ni téléchargement, ni permission ; toute navigation est annulée (un lien cliqué passe par
/// une confirmation, puis le navigateur par défaut) ; seules les adresses locales sont servies (page, images jointes,
/// feuille de style), le reste est refusé, sauf les images distantes d'un mail pour lequel l'utilisateur les a demandées.
/// Données dans <c>%LOCALAPPDATA%\QuaiMail\webview</c>, vidées au démarrage et à la fermeture.
/// </summary>
public sealed partial class ReaderView : UserControl
{
    /// <summary>Hôte virtuel du dossier <c>Assets/Mail</c> (feuille de style du gabarit).</summary>
    public const string ReaderHost = "reader.quaimail.example";

    private readonly ConcurrentQueue<string> _refused = new();
    private readonly ILogger _logger;
    private readonly string _dataFolder;
    private string? _expectedPage;
    private bool _initializing;

    public ReaderView()
    {
        var services = App.Current.Services;
        Reader = services.GetRequiredService<ReaderViewModel>();
        _logger = services.GetRequiredService<ILogger<ReaderView>>();
        _dataFolder = services.GetRequiredService<AppPaths>().WebViewDirectory;
        InitializeComponent();
        RuntimeMissing.IsOpen = false;
        Reader.PageChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowPage);
        ActualThemeChanged += (_, _) => ApplyTheme();
        Loaded += async (_, _) => await InitializeAsync();
    }

    public ReaderViewModel Reader { get; }

    /// <summary>WebView2 prête (moteur trouvé, réglages verrouillés).</summary>
    public bool IsReady => Body.CoreWebView2 is not null && !_initializing;

    /// <summary>Test de fumée : les images distantes autorisées sont tout de même refusées (aucun réseau), mais notées.</summary>
    public bool OfflineImages { get; set; }

    /// <summary>Dernière page chargée avec succès (adresse locale).</summary>
    public string? LoadedPage { get; private set; }

    /// <summary>Navigations annulées (liens, redirections, cadres, nouvelles fenêtres).</summary>
    public int CancelledNavigations { get; private set; }

    /// <summary>Adresses servies par l'hôte local (page, images jointes).</summary>
    public int ServedRequests { get; private set; }

    /// <summary>Images distantes laissées passer (mail pour lequel l'utilisateur a demandé les images).</summary>
    public int AllowedRemoteImages { get; private set; }

    /// <summary>Requêtes refusées (hors de l'hôte local) : leur adresse, pour le test de fumée.</summary>
    public IReadOnlyCollection<string> RefusedRequests => _refused;

    public CoreWebView2Settings? Settings => Body.CoreWebView2?.Settings;

    /// <summary>Fermeture : la WebView2 s'arrête et ses données (cookies, cache) sont effacées.</summary>
    public void Shutdown()
    {
        try
        {
            Body.Close();
        }
        catch (Exception)
        {
            // Déjà fermée.
        }

        ClearDataFolder();
    }

    /// <summary>Test de fumée : demande une navigation vers une adresse quelconque (elle doit être annulée).</summary>
    internal void TryNavigate(string uri) => Body.CoreWebView2?.Navigate(uri);

    private async Task InitializeAsync()
    {
        if (Body.CoreWebView2 is not null || _initializing)
        {
            return;
        }

        _initializing = true;
        try
        {
            try
            {
                _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                // Runtime WebView2 absent : message clair et lien officiel, rien n'est téléchargé en douce.
                _logger.LogWarning("Runtime WebView2 introuvable ({Error})", ex.GetType().Name);
                RuntimeMissing.IsOpen = true;
                return;
            }

            ClearDataFolder();
            var options = new CoreWebView2EnvironmentOptions
            {
                AllowSingleSignOnUsingOSPrimaryAccount = false,
                AreBrowserExtensionsEnabled = false,
                IsCustomCrashReportingEnabled = true,
            };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, _dataFolder, options);
            await Body.EnsureCoreWebView2Async(environment);
            Lock(Body.CoreWebView2, environment);
            _logger.LogInformation("Lecture : WebView2 {Version} prête, verrouillée", environment.BrowserVersionString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebView2 de lecture impossible à préparer");
            RuntimeMissing.IsOpen = true;
        }
        finally
        {
            _initializing = false;
        }

        ShowPage();
    }

    private void Lock(CoreWebView2 core, CoreWebView2Environment environment)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsReputationCheckingRequired = false;

        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
        ApplyTheme();

        // Feuille de style du gabarit (Assets/Mail), servie par un nom d'hôte virtuel.
        core.SetVirtualHostNameToFolderMapping(ReaderHost, Path.Combine(AppContext.BaseDirectory, "Assets", "Mail"), CoreWebView2HostResourceAccessKind.Deny);

        // Toutes les requêtes passent par ici : la page et les images jointes sont servies depuis la mémoire, le reste
        // est refusé (seconde barrière après la politique CSP du gabarit).
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) => OnResourceRequested(environment, args);

        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri == _expectedPage)
            {
                return;
            }

            args.Cancel = true;
            CancelledNavigations++;
            if (args.IsUserInitiated)
            {
                OpenLink(args.Uri);
            }
        };
        core.FrameNavigationStarting += (_, args) =>
        {
            args.Cancel = true;
            CancelledNavigations++;
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            CancelledNavigations++;
            if (args.IsUserInitiated)
            {
                OpenLink(args.Uri);
            }
        };
        core.LaunchingExternalUriScheme += (_, args) =>
        {
            args.Cancel = true;
            CancelledNavigations++;
            if (args.IsUserInitiated)
            {
                OpenLink(args.Uri);
            }
        };
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
        };
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ContextMenuRequested += (_, args) => args.Handled = true;
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess && core.Source == _expectedPage)
            {
                LoadedPage = _expectedPage;
            }
        };
        core.ProcessFailed += (_, args) => _logger.LogWarning("WebView2 de lecture : processus arrêté ({Kind})", args.ProcessFailedKind);
    }

    private void OnResourceRequested(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
        {
            args.Response = environment.CreateWebResourceResponse(null, 400, "Bad Request", string.Empty);
            return;
        }

        if (uri.Host == ReaderHost && uri.Scheme == "https" && args.Request.Method == "GET")
        {
            // Dossier Assets/Mail (hôte virtuel) : rien à faire ici.
            return;
        }

        if (uri.Host == HtmlCleaner.MessageHost && uri.Scheme == "https")
        {
            ServeLocal(environment, args, uri);
            return;
        }

        if (Reader.RemoteImagesShown && args.ResourceContext == CoreWebView2WebResourceContext.Image && uri.Scheme is "http" or "https")
        {
            AllowedRemoteImages++;
            if (!OfflineImages)
            {
                // Image distante d'un mail pour lequel l'utilisateur les a demandées.
                return;
            }
        }

        _refused.Enqueue(uri.Host);
        args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
    }

    private void ServeLocal(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs args, Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.StartsWith("/page/", StringComparison.Ordinal) && uri.AbsoluteUri == _expectedPage && Reader.Page is { Length: > 0 } page)
        {
            ServedRequests++;
            args.Response = Respond(environment, System.Text.Encoding.UTF8.GetBytes(page), "text/html; charset=utf-8", page: true);
            return;
        }

        if (path.StartsWith("/cid/", StringComparison.Ordinal) && Reader.Content is { } content)
        {
            var deferral = args.GetDeferral();
            _ = Task.Run(async () =>
            {
                try
                {
                    var id = Uri.UnescapeDataString(path["/cid/".Length..]);
                    var image = await content.GetImageAsync(id);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            if (image is { } found && found.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                && !found.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
                            {
                                ServedRequests++;
                                args.Response = Respond(environment, found.Data, found.ContentType, page: false);
                            }
                            else
                            {
                                args.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
                            }
                        }
                        finally
                        {
                            deferral.Complete();
                        }
                    });
                }
                catch (Exception)
                {
                    DispatcherQueue.TryEnqueue(deferral.Complete);
                }
            });
            return;
        }

        args.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
    }

    private static CoreWebView2WebResourceResponse Respond(CoreWebView2Environment environment, byte[] content, string contentType, bool page)
    {
        var headers = $"Content-Type: {contentType}\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store";
        if (page)
        {
            // Barrière en plus de la politique du gabarit (les deux s'appliquent) : ni script, ni cadre, ni formulaire.
            headers += "\r\nContent-Security-Policy: script-src 'none'; object-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";
        }

        return environment.CreateWebResourceResponse(new MemoryStream(content).AsRandomAccessStream(), 200, "OK", headers);
    }

    private void ShowPage()
    {
        if (Body.CoreWebView2 is not { } core)
        {
            return;
        }

        LoadedPage = null;
        if (string.IsNullOrEmpty(Reader.Page))
        {
            // Aucun mail : la WebView2 est masquée (l'ancien mail ne reste pas visible derrière l'état vide).
            _expectedPage = null;
            Body.Visibility = Visibility.Collapsed;
            return;
        }

        Body.Visibility = Visibility.Visible;
        _expectedPage = $"{HtmlCleaner.MessageOrigin}/page/{Reader.PageNumber}";
        core.Navigate(_expectedPage);
    }

    private async void OpenLink(string address)
    {
        try
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                await Reader.OpenLinkAsync(uri);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lien non ouvert ({Error})", ex.GetType().Name);
        }
    }

    private void ApplyTheme()
    {
        if (Body.CoreWebView2 is { } core)
        {
            core.Profile.PreferredColorScheme = ActualTheme == ElementTheme.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }
    }

    private void ClearDataFolder()
    {
        if (!Directory.Exists(_dataFolder))
        {
            return;
        }

        try
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Encore utilisé par un processus de la WebView2 : effacé au prochain démarrage.
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var mailbox = App.Current.Services.GetRequiredService<MailboxViewModel>();
            if (mailbox.Messages.SelectedMessage is { } message && message.Uid == Reader.Uid)
            {
                await mailbox.DeleteAsync(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suppression impossible");
        }
    }

    private void OnReplyClick(object sender, RoutedEventArgs e) => Respond(c => c.ReplyAsync(Reader.Content!, Reader.Account!, all: false));

    private void OnReplyAllClick(object sender, RoutedEventArgs e) => Respond(c => c.ReplyAsync(Reader.Content!, Reader.Account!, all: true));

    private void OnForwardClick(object sender, RoutedEventArgs e) => Respond(c => c.ForwardAsync(Reader.Content!, Reader.Account!));

    private async void Respond(Func<ComposerViewModel, Task<bool>> action)
    {
        try
        {
            if (Reader.Content is not null && Reader.Account is not null)
            {
                await action(App.Current.Services.GetRequiredService<ComposerViewModel>());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Réponse impossible à préparer");
        }
    }

    private async void OnAttachmentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is AttachmentInfo attachment)
            {
                await Reader.SaveAttachmentAsync(attachment);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Pièce jointe non enregistrée ({Error})", ex.GetType().Name);
        }
    }
}
