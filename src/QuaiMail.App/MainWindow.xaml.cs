using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QuaiMail.App.Services;
using QuaiMail.Core;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.ViewModels;
using Windows.Graphics;

namespace QuaiMail.App;

/// <summary>Fenêtre principale : barre de titre, bandeau de mise à jour, trois volets (comptes et dossiers, liste, lecture).</summary>
public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MainWindow> _logger;
    private readonly DialogService _dialogs;
    private readonly SettingsViewModel _settings;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _autoSave;
    private readonly IMailWatcher _watcher;
    private readonly INotifier _notifier;
    private bool _isActive = true;

    // Fermeture : question en cours, puis fermeture accordée (brouillon gardé ou supprimé).
    private bool _closeRequested;
    private bool _closeAllowed;

    public MainWindow()
    {
        _services = App.Current.Services;
        Shell = _services.GetRequiredService<ShellViewModel>();
        Updates = _services.GetRequiredService<UpdateBannerViewModel>();
        Mailbox = _services.GetRequiredService<MailboxViewModel>();
        Composer = _services.GetRequiredService<ComposerViewModel>();
        _settings = _services.GetRequiredService<SettingsViewModel>();
        _dialogs = _services.GetRequiredService<DialogService>();
        _logger = _services.GetRequiredService<ILogger<MainWindow>>();
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

        // Les boîtes de dialogue et sélecteurs de fichiers ont besoin de la fenêtre.
        _dialogs.Root = RootGrid;
        _services.GetRequiredService<FileDialogs>().WindowId = AppWindow.Id;
        _services.GetRequiredService<ThemeService>().Attach(this, RootGrid);

        AppWindow.Closing += OnClosing;
        Closed += (_, _) => ReaderPane.Shutdown();

        // Brouillon enregistré en local toutes les 5 secondes (et copié sur le serveur toutes les 30 s).
        _autoSave = DispatcherQueue.CreateTimer();
        _autoSave.Interval = TimeSpan.FromSeconds(5);
        _autoSave.Tick += async (_, _) =>
        {
            try
            {
                if (Composer.IsOpen)
                {
                    await Composer.AutoSaveAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enregistrement automatique du brouillon interrompu");
            }
        };
        _autoSave.Start();

        // Réception en direct : nouveaux mails dans la liste, les compteurs, et une notification si la fenêtre n'est pas au premier plan.
        _watcher = _services.GetRequiredService<IMailWatcher>();
        _notifier = _services.GetRequiredService<INotifier>();
        _watcher.NewMail += (_, e) => DispatcherQueue.TryEnqueue(() => OnNewMail(e));
        _notifier.Invoked += (_, accountId) => DispatcherQueue.TryEnqueue(() => OnNotificationClicked(accountId));
        var accounts = _services.GetRequiredService<AccountStore>();
        accounts.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => _watcher.Watch(accounts.All));
        Activated += (_, e) => _isActive = e.WindowActivationState != WindowActivationState.Deactivated;
        Closed += async (_, _) =>
        {
            try
            {
                await _watcher.DisposeAsync();
            }
            catch (Exception)
            {
                // Fermeture au mieux.
            }
        };
        ResizeForDpi(1240, 820);
    }

    /// <summary>Compte, dossier, mail ouvert, brouillon en cours.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>Bandeau « nouvelle version disponible ».</summary>
    public UpdateBannerViewModel Updates { get; }

    /// <summary>Volet des comptes et dossiers.</summary>
    public MailboxViewModel Mailbox { get; }

    /// <summary>Message en cours d'écriture.</summary>
    public ComposerViewModel Composer { get; }

    /// <summary>Liste des mails du dossier choisi.</summary>
    public MessageListViewModel Messages => Mailbox.Messages;

    /// <summary>Volet de lecture (WebView2 verrouillée).</summary>
    internal Views.ReaderView Reader => ReaderPane;

    /// <summary>Contenu racine de la fenêtre (test de fumée).</summary>
    public FrameworkElement Root => RootGrid;

    /// <summary>Ouvre les réglages (bouton du volet de gauche, test de fumée).</summary>
    internal Task ShowSettingsAsync() => _dialogs.ShowSettingsAsync(_settings, Mailbox);

    /// <summary>Démarre la réception en direct (après le premier chargement des comptes).</summary>
    internal void StartWatching() => _watcher.Watch(_services.GetRequiredService<AccountStore>().All);

    private void OnNewMail(NewMailEventArgs e)
    {
        var unread = Mailbox.OnNewMail(e.Account, e.Messages);
        if (unread.Count > 0 && !_isActive && _services.GetRequiredService<Core.Storage.ISettingsService>().Current.ShowNotifications)
        {
            _notifier.Show(e.Account, unread);
        }
    }

    /// <summary>Clic sur une notification : la fenêtre revient, sur la boîte de réception du compte.</summary>
    private void OnNotificationClicked(string accountId)
    {
        Activate();
        if (Mailbox.Items.FirstOrDefault(i => i.AccountId == accountId && i.Role == Core.Mail.FolderRole.Inbox && i.CanSelect) is { } inbox)
        {
            Mailbox.SelectedItem = inbox;
        }
    }

    /// <summary>« Ajouter un compte » : formulaire, vérification, puis le compte apparaît avec sa boîte de réception ouverte.</summary>
    internal async Task<Account?> AddAccountAsync()
    {
        var editor = _services.GetRequiredService<AccountEditorViewModel>();
        if (await _dialogs.ShowAccountDialogAsync(editor) is { } account)
        {
            await Mailbox.OnAccountAddedAsync(account);
            return account;
        }

        return null;
    }

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

            // Gardé : aussi dans Brouillons du serveur (au mieux, sans faire attendre plus de 10 s).
            if (Shell.HasDraft)
            {
                await Task.WhenAny(Composer.SyncServerAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
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

    private async void OnNewMessageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Composer.NewAsync(Mailbox.Messages.AccountId ?? Mailbox.SelectedItem?.AccountId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nouveau message impossible");
        }
    }

    private async void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await AddAccountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ajout de compte interrompu");
        }
    }

    private async void OnRemoveAccountClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is NavItemViewModel item)
            {
                await Mailbox.RemoveAccountAsync(item.AccountId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retrait du compte interrompu");
        }
    }

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        try
        {
            await Messages.SearchAsync(args.QueryText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recherche impossible");
        }
    }

    /// <summary>Champ vidé : retour au dossier.</summary>
    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        try
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text) && Messages.IsSearching)
            {
                await Messages.EndSearchAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fin de recherche impossible");
        }
    }

    private async void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await DeleteAsync(Messages.SelectedMessage);
    }

    private async void OnDeleteMessageClick(object sender, RoutedEventArgs e) =>
        await DeleteAsync((sender as FrameworkElement)?.DataContext as MessageItemViewModel);

    /// <summary>Supprimer : corbeille, ou définitif après confirmation (voir MailboxViewModel.DeleteAsync).</summary>
    internal async Task DeleteAsync(MessageItemViewModel? message)
    {
        try
        {
            if (message is not null)
            {
                await Mailbox.DeleteAsync(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suppression impossible");
        }
    }

    /// <summary>Menu d'un mail : « Déplacer vers » liste les dossiers de son compte.</summary>
    private void OnMessageMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault() is not { } moveTo
            || menu.Target?.DataContext is not MessageItemViewModel message)
        {
            return;
        }

        moveTo.Items.Clear();
        foreach (var target in Mailbox.MoveTargets(message))
        {
            var item = new MenuFlyoutItem { Text = target.Title };
            item.Click += async (_, _) =>
            {
                try
                {
                    await Mailbox.MoveAsync(message, target);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Déplacement impossible");
                }
            };
            moveTo.Items.Add(item);
        }

        moveTo.IsEnabled = moveTo.Items.Count > 0;
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Mailbox.SelectedItem = NavList.SelectedItem as NavItemViewModel;

    /// <summary>Mail choisi : ouvert dans le volet de lecture (voir MailboxViewModel).</summary>
    private void OnMessageSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Messages.SelectedMessage = MessageList.SelectedItem as MessageItemViewModel;

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
