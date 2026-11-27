using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuaiMail.App.Localization;
using QuaiMail.App.Services;
using QuaiMail.Core;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.ViewModels;
using Windows.Graphics;
using Windows.System;

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

        // Clic n'importe où sur la ligne d'un compte : replier / déplier (la liste a déjà traité le clic pour la sélection).
        NavList.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnNavTapped), true);
        Mailbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MailboxViewModel.SelectedItem))
            {
                DispatcherQueue.TryEnqueue(SyncNavSelection);
            }
            else if (e.PropertyName == nameof(MailboxViewModel.IsPaneCompact))
            {
                ApplyPaneMode();
            }
        };
        ApplyPaneMode();

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

    /// <summary>Ligne en surbrillance dans le volet de gauche (test de fumée).</summary>
    internal object? NavSelection => NavList.SelectedItem;

    /// <summary>Ouvre les réglages (bouton du volet de gauche, test de fumée).</summary>
    internal Task ShowSettingsAsync() => _dialogs.ShowSettingsAsync(
        _settings, Mailbox, _services.GetRequiredService<PgpKeysViewModel>(), _services.GetRequiredService<TrustedSendersViewModel>());

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
        Mailbox.ShowInbox(accountId);
    }

    /// <summary>« Ajouter un compte » : formulaire, vérification, puis le compte apparaît avec sa boîte de réception ouverte.</summary>
    internal async Task<Account?> AddAccountAsync()
    {
        var editor = _services.GetRequiredService<AccountEditorViewModel>();
        if (await _dialogs.ShowAccountDialogAsync(editor) is { } account)
        {
            // Après une connexion dans le navigateur, Quai Mail revient au premier plan (si Windows le permet).
            Activate();
            await Mailbox.OnAccountAddedAsync(account);
            return account;
        }

        return null;
    }

    /// <summary>
    /// « Se reconnecter » (compte Gmail ou Outlook.com dont l'autorisation a expiré) : page de connexion dans le
    /// navigateur, puis le compte reprend (dossiers, réception en direct).
    /// </summary>
    internal async Task<bool> ReconnectAccountAsync(string accountId)
    {
        var accounts = _services.GetRequiredService<AccountStore>();
        if (accounts.Find(accountId) is not { } account)
        {
            return false;
        }

        var editor = _services.GetRequiredService<AccountEditorViewModel>();
        Mailbox.ErrorMessage = null;
        if (!await editor.ReconnectAsync(account))
        {
            Mailbox.ErrorMessage = _services.GetRequiredService<IStrings>().Format("Mailbox_AccountError", account.Title, editor.ErrorMessage ?? string.Empty);
            return false;
        }

        Activate();
        StartWatching();
        await Mailbox.OnAccountAddedAsync(account);
        return true;
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

    private async void OnReconnectAccountClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is NavItemViewModel item)
            {
                await ReconnectAccountAsync(item.AccountId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnexion du compte interrompue");
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

    /// <summary>Largeur du volet de gauche (déployé ou rail d'icônes), pour le test de fumée.</summary>
    internal double NavPaneWidth => NavPane.ActualWidth;

    /// <summary>Volet de gauche déployé (dossiers) ou réduit en rail d'icônes : le style porte largeur et marges.</summary>
    private void ApplyPaneMode()
    {
        var key = Mailbox.IsPaneCompact ? "FolderRailStyle" : "FolderPaneStyle";
        if (Application.Current.Resources.TryGetValue(key, out var style) && style is Style paneStyle)
        {
            NavPane.Style = paneStyle;
        }
    }

    private void OnTogglePaneAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Mailbox.TogglePaneCommand.Execute(null);
    }

    private void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Mailbox.HasAccounts && Mailbox.RefreshCommand.CanExecute(null))
        {
            Mailbox.RefreshCommand.Execute(null);
        }
    }

    /// <summary>Rail d'icônes : un clic sur un compte (ou la boîte unifiée) ouvre sa boîte de réception, même s'il est déjà en surbrillance.</summary>
    private void OnRailItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NavItemViewModel item)
        {
            Mailbox.ShowInbox(item.AccountId);
        }
    }

    /// <summary>Rail d'icônes, flèches haut / bas : ouvre la boîte de réception du compte choisi (la surbrillance qui suit le dossier affiché ne fait rien).</summary>
    private void OnRailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RailList.SelectedItem is NavItemViewModel item && item.AccountId != Mailbox.SelectedRailItem?.AccountId)
        {
            Mailbox.ShowInbox(item.AccountId);
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

    /// <summary>Suppr dans la liste : les mails choisis (un seul, ou toute la sélection).</summary>
    private async void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFocusInMessageList())
        {
            return;
        }

        args.Handled = true;
        await DeleteAsync(Messages.SelectedMessages);
    }

    /// <summary>Ctrl+A dans la liste : tous les mails affichés sont choisis (aucun ne s'ouvre).</summary>
    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFocusInMessageList())
        {
            return;
        }

        args.Handled = true;
        SelectAllMessages();
    }

    /// <summary>
    /// Le focus est-il dans la liste des mails ? Ses raccourcis (Suppr, Ctrl+A) n'agissent que là : ailleurs (recherche,
    /// message en cours d'écriture, Réglages), la touche n'est pas prise et garde son rôle habituel dans le champ.
    /// </summary>
    private bool IsFocusInMessageList()
    {
        var element = MessageList.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        while (element is not null)
        {
            if (ReferenceEquals(element, MessageList))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    /// <summary>Mails visés par le menu ouvert : la sélection si le mail cliqué en fait partie, sinon lui seul.</summary>
    private IReadOnlyList<MessageItemViewModel> _menuTargets = [];

    private async void OnDeleteMessageClick(object sender, RoutedEventArgs e) => await DeleteAsync(_menuTargets);

    private async void OnMarkReadClick(object sender, RoutedEventArgs e) => await SetReadAsync(_menuTargets, read: true);

    private async void OnMarkUnreadClick(object sender, RoutedEventArgs e) => await SetReadAsync(_menuTargets, read: false);

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAllMessages();

    /// <summary>Choisit tous les mails affichés (Ctrl+A, clic droit, test de fumée).</summary>
    internal void SelectAllMessages() => MessageList.SelectAll();

    /// <summary>Bouton du filtre lu / non lu affiché (test de fumée).</summary>
    internal bool IsReadFilterShown => ReadFilterButton.Visibility == Visibility.Visible;

    /// <summary>Choisit un filtre par la commande de son vrai menu : 0 = tous, 1 = non lus, 2 = lus (test de fumée).</summary>
    internal void ChooseReadFilter(int index)
    {
        var item = (MenuFlyoutItem)((MenuFlyout)ReadFilterButton.Flyout).Items[index];
        item.Command.Execute(item.CommandParameter);
    }

    /// <summary>Choisit des mails voisins, comme Maj + clic (test de fumée).</summary>
    internal void SelectMessageRange(int first, int count)
    {
        MessageList.SelectedItems.Clear();
        MessageList.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(first, (uint)count));
    }

    /// <summary>Supprimer : corbeille, ou définitif après confirmation (voir MailboxViewModel.DeleteAsync).</summary>
    internal async Task DeleteAsync(IReadOnlyList<MessageItemViewModel> messages)
    {
        try
        {
            if (messages.Count > 0)
            {
                await Mailbox.DeleteAsync(messages);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suppression impossible");
        }
    }

    /// <summary>Marquer lus ou non lus (clic droit sur un mail ou une sélection).</summary>
    internal async Task SetReadAsync(IReadOnlyList<MessageItemViewModel> messages, bool read)
    {
        try
        {
            if (messages.Count > 0)
            {
                await Mailbox.SetReadAsync(messages, read);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lu / non lu impossible");
        }
    }

    /// <summary>
    /// Menu d'un mail : il vise toute la sélection si ce mail en fait partie, sinon lui seul. Les textes donnent le
    /// nombre de mails, « lu » et « non lu » n'apparaissent que s'ils changent quelque chose, « Déplacer vers » liste
    /// les dossiers du compte (aucun si la sélection mêle plusieurs comptes).
    /// </summary>
    private void OnMessageMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target?.DataContext is not MessageItemViewModel message)
        {
            return;
        }

        var targets = _menuTargets = Messages.TargetsOf(message);
        var count = targets.Count;
        foreach (var entry in menu.Items)
        {
            switch (entry)
            {
                case MenuFlyoutItem { Tag: "MarkRead" } markRead:
                    markRead.Text = count > 1 ? Loc.Format("Messages_MarkReadMany", count) : Loc.Get("Messages_MarkRead");
                    markRead.Visibility = UiHelpers.VisibleIf(targets.Any(m => !m.IsRead));
                    break;
                case MenuFlyoutItem { Tag: "MarkUnread" } markUnread:
                    markUnread.Text = count > 1 ? Loc.Format("Messages_MarkUnreadMany", count) : Loc.Get("Messages_MarkUnread");
                    markUnread.Visibility = UiHelpers.VisibleIf(targets.Any(m => m.IsRead));
                    break;
                case MenuFlyoutItem { Tag: "Delete" } delete:
                    delete.Text = count > 1 ? Loc.Format("Messages_DeleteMany", count) : Loc.Get("Messages_Delete");
                    break;
                case MenuFlyoutSubItem moveTo:
                    moveTo.Text = count > 1 ? Loc.Format("Messages_MoveToMany", count) : Loc.Get("Messages_MoveTo");
                    FillMoveTargets(moveTo, targets);
                    break;
            }
        }
    }

    private void FillMoveTargets(MenuFlyoutSubItem moveTo, IReadOnlyList<MessageItemViewModel> messages)
    {
        moveTo.Items.Clear();
        foreach (var target in Mailbox.MoveTargets(messages))
        {
            var item = new MenuFlyoutItem { Text = target.Title };
            item.Click += async (_, _) =>
            {
                try
                {
                    await Mailbox.MoveAsync(messages, target);
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

    /// <summary>
    /// Dossier choisi (clic, flèches haut / bas) : affiché. La ligne d'un compte ne choisit rien (son clic le replie ou
    /// le déplie, voir <see cref="OnNavTapped"/>), et un dossier qui quitte le volet (compte replié) reste affiché : la
    /// surbrillance revient sur le dossier affiché, ou disparaît s'il est masqué.
    /// </summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is NavItemViewModel { IsAccount: false } item)
        {
            Mailbox.SelectedItem = item;
        }
        else
        {
            DispatcherQueue.TryEnqueue(SyncNavSelection);
        }
    }

    /// <summary>Surbrillance du volet = dossier affiché s'il est visible (compte déplié), sinon aucune.</summary>
    private void SyncNavSelection()
    {
        var shown = Mailbox.SelectedItem is { } item && Mailbox.Items.Contains(item) ? item : null;
        if (!ReferenceEquals(NavList.SelectedItem, shown))
        {
            NavList.SelectedItem = shown;
        }
    }

    private void OnNavTapped(object sender, TappedRoutedEventArgs e)
    {
        if (NavItemFrom(e.OriginalSource) is { IsAccount: true } account)
        {
            DispatcherQueue.TryEnqueue(() => SetAccountExpanded(account.AccountId, !account.IsExpanded));
        }
    }

    /// <summary>
    /// Clavier sur la ligne d'un compte : Entrée ou Espace replient ou déplient, flèche gauche replie, flèche droite
    /// déplie ; flèche gauche sur un dossier remonte à la ligne de son compte (comme l'Explorateur).
    /// </summary>
    private void OnNavPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (NavItemFrom(e.OriginalSource) is not { IsUnified: false } item)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter or VirtualKey.Space when item.IsAccount:
                SetAccountExpanded(item.AccountId, !item.IsExpanded);
                break;
            case VirtualKey.Left when item.IsAccount && item.IsExpanded:
                SetAccountExpanded(item.AccountId, false);
                break;
            case VirtualKey.Right when item.IsAccount && !item.IsExpanded:
                SetAccountExpanded(item.AccountId, true);
                break;
            case VirtualKey.Left when !item.IsAccount
                && Mailbox.Items.FirstOrDefault(i => i.IsAccount && i.AccountId == item.AccountId) is { } account
                && NavList.ContainerFromItem(account) is ListViewItem container:
                container.Focus(FocusState.Keyboard);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Replie ou déplie un compte du volet de gauche (clic, clavier, test de fumée).</summary>
    internal void SetAccountExpanded(string accountId, bool expanded)
    {
        try
        {
            Mailbox.SetExpanded(accountId, expanded);
            SyncNavSelection();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compte non replié / déplié");
        }
    }

    /// <summary>Ligne du volet sous un élément (clic, touche) : remonte jusqu'à son conteneur de la liste.</summary>
    private NavItemViewModel? NavItemFrom(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null and not ListViewItem && !ReferenceEquals(element, NavList))
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element is ListViewItem container ? NavList.ItemFromContainer(container) as NavItemViewModel : null;
    }

    /// <summary>
    /// Sélection changée : un seul mail choisi s'ouvre dans le volet de lecture (voir MailboxViewModel) ; plusieurs
    /// (Ctrl+A, Maj / Ctrl + clic) ne changent pas le mail ouvert.
    /// </summary>
    private void OnMessageSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Messages.SetSelection(MessageList.SelectedItems.OfType<MessageItemViewModel>());

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
