using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Accounts.OAuth;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Volet de gauche : les comptes et leurs dossiers. Les dossiers en cache s'affichent tout de suite, puis chaque compte
/// est relu sur son serveur. Choisir un dossier affiche ses mails (<see cref="MessageListViewModel"/>) et le note dans
/// l'état de la fenêtre (<see cref="ShellViewModel"/>). Chaque compte se replie : ses dossiers quittent le volet
/// (<see cref="Items"/>) mais restent connus (compteurs, « Déplacer vers », notifications) ; l'état est gardé dans les
/// réglages.
/// </summary>
public sealed partial class MailboxViewModel : ObservableObject
{
    private readonly AccountStore _accounts;
    private readonly ICredentialStore _credentials;
    private readonly AccessTokens? _tokens;
    private readonly IMailService _mail;
    private readonly MailCache _cache;
    private readonly ShellViewModel _shell;
    private readonly IDialogService _dialogs;
    private readonly IStrings _strings;
    private readonly ILogger _logger;
    private readonly ISettingsService? _settings;
    private readonly HashSet<string> _collapsed;
    private List<NavItemViewModel> _all = [];
    private bool _rebuilding;

    public MailboxViewModel(
        AccountStore accounts,
        ICredentialStore credentials,
        IMailService mail,
        MailCache cache,
        ShellViewModel shell,
        MessageListViewModel messages,
        ReaderViewModel reader,
        IDialogService dialogs,
        IStrings strings,
        ILogger<MailboxViewModel>? logger = null,
        AccessTokens? tokens = null,
        ISettingsService? settings = null)
    {
        _tokens = tokens;
        _settings = settings;
        _collapsed = new HashSet<string>(settings?.Current.CollapsedAccounts ?? [], StringComparer.Ordinal);
        _accounts = accounts;
        _credentials = credentials;
        _mail = mail;
        _cache = cache;
        _shell = shell;
        Messages = messages;
        Reader = reader;
        _dialogs = dialogs;
        _strings = strings;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Messages.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageListViewModel.SelectedMessage))
            {
                OnMessageSelected(Messages.SelectedMessage);
            }
        };
        Reader.HeaderChanged += (_, change) => OnHeaderChanged(change);
    }

    /// <summary>Lignes affichées : la boîte unifiée, chaque compte, et les dossiers des comptes dépliés.</summary>
    public ObservableCollection<NavItemViewModel> Items { get; } = [];

    /// <summary>Toutes les lignes, dossiers des comptes repliés compris.</summary>
    public IReadOnlyList<NavItemViewModel> AllItems => _all;

    public MessageListViewModel Messages { get; }

    /// <summary>Volet de lecture.</summary>
    public ReaderViewModel Reader { get; }

    [ObservableProperty]
    public partial NavItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAccount))]
    public partial bool HasAccounts { get; set; }

    public bool HasNoAccount => !HasAccounts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; set; }

    /// <summary>Dernière erreur de connexion (un compte injoignable n'empêche pas les autres de s'afficher).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Premier affichage : cache, sélection (restaurée après une mise à jour, sinon la première boîte de réception), puis serveurs.</summary>
    public async Task LoadAsync()
    {
        Rebuild();
        SelectInitial();
        await RefreshAsync();
    }

    /// <summary>Relit les dossiers de chaque compte sur son serveur.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        ErrorMessage = null;
        try
        {
            foreach (var account in _accounts.All)
            {
                try
                {
                    var folders = await _mail.GetFoldersAsync(account);
                    _cache.SaveFolders(account.Id, folders);
                }
                catch (MailConnectionException ex)
                {
                    ErrorMessage = _strings.Format("Mailbox_AccountError", account.Title, MailErrors.Describe(_strings, ex));
                }
            }

            Rebuild();
            if (SelectedItem is null)
            {
                SelectInitial();
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Compte tout juste ajouté (ou reconnecté) : affiché et sa boîte de réception choisie.</summary>
    public async Task OnAccountAddedAsync(Account account)
    {
        try
        {
            var folders = await _mail.GetFoldersAsync(account);
            _cache.SaveFolders(account.Id, folders);
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = _strings.Format("Mailbox_AccountError", account.Title, MailErrors.Describe(_strings, ex));
        }

        Rebuild();
        SelectedItem = InboxOf(account.Id) ?? SelectedItem;
    }

    /// <summary>Supprime un compte de Quai Mail (après confirmation) : réglages, mot de passe et cache. Rien n'est touché sur le serveur.</summary>
    public async Task<bool> RemoveAccountAsync(string accountId)
    {
        if (_accounts.Find(accountId) is not { } account)
        {
            return false;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            _strings.Get("RemoveAccount_Title"),
            _strings.Format("RemoveAccount_Message", account.Title),
            _strings.Get("RemoveAccount_Confirm"),
            isDestructive: true);
        if (!confirmed)
        {
            return false;
        }

        await _mail.DisconnectAsync(accountId);
        _accounts.Remove(accountId);
        _credentials.Remove(accountId);
        _tokens?.Forget(accountId);
        _cache.RemoveAccount(accountId);
        if (_collapsed.Remove(accountId))
        {
            SaveCollapsed();
        }

        _logger.LogInformation("Compte {Id} retiré de Quai Mail", accountId);

        if (Messages.AccountId == accountId)
        {
            Reader.Clear();
            Messages.Clear();
            _shell.SelectedAccountId = null;
            _shell.SelectedFolderPath = null;
            _shell.OpenMessageId = null;
        }

        Rebuild();
        if (SelectedItem is null)
        {
            SelectInitial();
        }

        return true;
    }

    /// <summary>
    /// Efface le cache local puis relit tout sur les serveurs : rien n'est perdu, le serveur est la référence.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        var shown = Messages.AccountId is { } account && Messages.FolderName is { } folder ? (account, folder) : default;
        _cache.Clear();
        await RefreshAsync();
        if (shown != default && _all.FirstOrDefault(i => i.Folder is { } f && (i.AccountId, f.FullName) == shown) is { } item
            && _accounts.Find(item.AccountId) is { } owner)
        {
            await ShowFolderAsync(owner, item.Folder!, item.Title);
        }
    }

    /// <summary>Replie ou déplie un compte (clic sur sa ligne, flèches gauche / droite).</summary>
    public void ToggleAccount(string accountId)
    {
        if (AccountLine(accountId) is { } account)
        {
            SetExpanded(accountId, !account.IsExpanded);
        }
    }

    /// <summary>
    /// Déplie (ses dossiers reviennent sous sa ligne) ou replie (ils quittent le volet) un compte, et garde ce choix dans
    /// les réglages. Le dossier affiché reste affiché, même replié.
    /// </summary>
    public void SetExpanded(string accountId, bool expanded)
    {
        if (AccountLine(accountId) is not { } account || account.IsExpanded == expanded)
        {
            return;
        }

        account.IsExpanded = expanded;
        if (expanded)
        {
            _collapsed.Remove(accountId);
            var index = Items.IndexOf(account) + 1;
            foreach (var folder in _all.Where(i => i.AccountId == accountId && !i.IsAccount))
            {
                Items.Insert(index++, folder);
            }
        }
        else
        {
            _collapsed.Add(accountId);
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].AccountId == accountId && !Items[i].IsAccount)
                {
                    Items.RemoveAt(i);
                }
            }
        }

        UpdateAccountUnread(accountId);
        SaveCollapsed();
    }

    /// <summary>Affiche la boîte de réception d'un compte (clic sur une notification), même si le compte est replié.</summary>
    public void ShowInbox(string accountId)
    {
        if (InboxOf(accountId) is { } inbox)
        {
            SelectedItem = inbox;
        }
    }

    /// <summary>Titre affiché d'un dossier : nom français des dossiers particuliers à la racine, sinon son nom.</summary>
    public string FolderTitle(MailFolderInfo folder) =>
        folder.Depth == 0 && folder.Role != FolderRole.None ? _strings.Get("Folder_" + folder.Role) : folder.Name;

    partial void OnSelectedItemChanged(NavItemViewModel? value)
    {
        if (_rebuilding || value is null)
        {
            return;
        }

        if (value.IsUnified)
        {
            if (_shell.SelectedAccountId != NavItemViewModel.UnifiedId)
            {
                _shell.OpenMessageId = null;
            }

            _shell.SelectedAccountId = NavItemViewModel.UnifiedId;
            _shell.SelectedFolderPath = "INBOX";
            Reader.Clear();
            _ = Messages.ShowUnifiedAsync(_accounts.All, value.Title);
            return;
        }

        if (!value.CanSelect)
        {
            // Ligne d'un compte (ou dossier conteneur) : sa boîte de réception à la place.
            if (value.IsAccount && InboxOf(value.AccountId) is { } inbox)
            {
                SelectedItem = inbox;
            }

            return;
        }

        if (_accounts.Find(value.AccountId) is not { } account || value.Folder is not { } folder)
        {
            return;
        }

        if (_shell.SelectedAccountId != account.Id || _shell.SelectedFolderPath != folder.FullName)
        {
            _shell.OpenMessageId = null;
        }

        _shell.SelectedAccountId = account.Id;
        _shell.SelectedFolderPath = folder.FullName;
        _ = ShowFolderAsync(account, folder, value.Title);
    }

    /// <summary>Affiche un dossier, puis rouvre le mail noté dans l'état de la fenêtre (après une mise à jour).</summary>
    private async Task ShowFolderAsync(Account account, MailFolderInfo folder, string title)
    {
        Reader.Clear();
        var toReopen = _shell.OpenMessageId;
        await Messages.ShowAsync(account, folder, title);
        if (toReopen is not null && Messages.FolderName == folder.FullName && Messages.SelectedMessage is null
            && Messages.Messages.FirstOrDefault(m => m.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture) == toReopen) is { } message)
        {
            Messages.SelectedMessage = message;
        }
    }

    private void OnMessageSelected(MessageItemViewModel? message)
    {
        if (message is null || _accounts.Find(message.AccountId) is not { } account)
        {
            return;
        }

        if (Reader.HasMessage && Reader.Uid == message.Uid && Reader.Account?.Id == account.Id && Reader.Folder == message.Folder)
        {
            return;
        }

        _shell.OpenMessageId = message.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = Reader.ShowAsync(account, message.Folder, message.Header);
    }

    /// <summary>Lu / non lu changé : ligne de la liste, cache et compteur du dossier.</summary>
    private void OnHeaderChanged(MessageChange change)
    {
        if (Messages.UpdateHeader(change.AccountId, change.Folder, change.Header) is not { } previous || previous.IsRead == change.Header.IsRead)
        {
            return;
        }

        AdjustUnread(change.AccountId, change.Folder, change.Header.IsRead ? -1 : 1);
    }

    private void AdjustUnread(string accountId, string folder, int delta)
    {
        if (_all.FirstOrDefault(i => i.AccountId == accountId && i.Folder?.FullName == folder) is { } item)
        {
            item.Unread = Math.Max(0, item.Unread + delta);
            UpdateAccountUnread(accountId);
        }

        if (folder == "INBOX" && _all.FirstOrDefault(i => i.IsUnified) is { } unified)
        {
            unified.Unread = Math.Max(0, unified.Unread + delta);
        }
    }

    /// <summary>Compte replié : sa ligne montre les non lus de ses dossiers (sans Indésirables ni Corbeille) ; déplié : rien.</summary>
    private void UpdateAccountUnread(string accountId)
    {
        if (AccountLine(accountId) is { } account)
        {
            account.Unread = account.IsExpanded
                ? 0
                : _all.Where(i => i.AccountId == accountId && i.CanSelect && i.Role is not (FolderRole.Junk or FolderRole.Trash)).Sum(i => i.Unread);
        }
    }

    private NavItemViewModel? AccountLine(string accountId) =>
        _all.FirstOrDefault(i => i.IsAccount && i.AccountId == accountId);

    private void SaveCollapsed() =>
        _settings?.Update(s => s.CollapsedAccounts = [.. _collapsed.Order(StringComparer.Ordinal)]);

    /// <summary>
    /// Réception en direct : nouveaux mails ajoutés en tête de la liste affichée et aux compteurs.
    /// </summary>
    /// <returns>Les mails non lus parmi eux (pour une notification).</returns>
    public IReadOnlyList<MessageHeader> OnNewMail(Account account, IReadOnlyList<MessageHeader> messages)
    {
        Messages.InsertNewest(account.Id, "INBOX", messages);
        var unread = messages.Where(m => !m.IsRead).ToList();
        if (unread.Count > 0)
        {
            AdjustUnread(account.Id, "INBOX", unread.Count);
        }

        return unread;
    }

    /// <summary>Dossiers vers lesquels un mail peut être déplacé (même compte, sauf son dossier).</summary>
    public IReadOnlyList<NavItemViewModel> MoveTargets(MessageItemViewModel message) => MoveTargets([message]);

    /// <summary>
    /// Dossiers vers lesquels une sélection peut être déplacée : ceux de son compte, sauf le dossier d'où viennent
    /// tous ces mails ; aucun si elle mêle plusieurs comptes (boîte unifiée, recherche).
    /// </summary>
    public IReadOnlyList<NavItemViewModel> MoveTargets(IReadOnlyList<MessageItemViewModel> messages)
    {
        if (messages.Count == 0 || messages.Any(m => m.AccountId != messages[0].AccountId))
        {
            return [];
        }

        var folders = messages.Select(m => m.Folder).Distinct().ToList();
        return [.. _all.Where(i => i.AccountId == messages[0].AccountId && i.CanSelect && !(folders.Count == 1 && i.Folder!.FullName == folders[0]))];
    }

    /// <summary>
    /// Supprimer : vers la corbeille du serveur ; s'il n'y en a pas, ou si le mail y est déjà, suppression définitive
    /// seulement après confirmation (« Annuler » par défaut).
    /// </summary>
    /// <returns><c>true</c> si le mail a quitté le dossier.</returns>
    public async Task<bool> DeleteAsync(MessageItemViewModel message) => await DeleteAsync([message]) == 1;

    /// <summary>
    /// Supprime une sélection (Ctrl+A, Maj / Ctrl + clic) : vers la corbeille de son compte, une commande par dossier ;
    /// les mails qui ne peuvent pas y aller (pas de corbeille, ou déjà dedans) ne sont supprimés pour de bon qu'après
    /// une seule confirmation pour eux tous (« Annuler » par défaut).
    /// </summary>
    /// <returns>Le nombre de mails qui ont quitté leur dossier.</returns>
    public async Task<int> DeleteAsync(IReadOnlyList<MessageItemViewModel> messages)
    {
        var removed = 0;
        var forever = new List<(Account Account, string Folder, List<MessageItemViewModel> Items)>();
        foreach (var (account, folder, items) in Groups(messages))
        {
            try
            {
                if (!await _mail.MoveToTrashAsync(account, folder, Uids(items)))
                {
                    forever.Add((account, folder, items));
                    continue;
                }
            }
            catch (MailConnectionException ex)
            {
                ShowError(account, ex);
                continue;
            }

            Removed(items);
            removed += items.Count;
        }

        if (forever.Count == 0)
        {
            return removed;
        }

        var all = forever.SelectMany(f => f.Items).ToList();
        var confirmed = await _dialogs.ConfirmAsync(
            _strings.Get("DeleteForever_Title"),
            all.Count == 1 ? _strings.Format("DeleteForever_Message", all[0].Subject) : _strings.Format("DeleteForever_MessageMany", all.Count),
            _strings.Get("DeleteForever_Confirm"),
            isDestructive: true);
        if (!confirmed)
        {
            return removed;
        }

        foreach (var (account, folder, items) in forever)
        {
            try
            {
                await _mail.DeletePermanentlyAsync(account, folder, Uids(items));
            }
            catch (MailConnectionException ex)
            {
                ShowError(account, ex);
                continue;
            }

            Removed(items);
            removed += items.Count;
        }

        return removed;
    }

    /// <summary>Déplace un mail vers un autre dossier de son compte.</summary>
    public async Task<bool> MoveAsync(MessageItemViewModel message, NavItemViewModel target) => await MoveAsync([message], target) == 1;

    /// <summary>Déplace une sélection vers un dossier de son compte (une commande par dossier d'origine).</summary>
    /// <returns>Le nombre de mails déplacés (ceux d'un autre compte ne bougent pas).</returns>
    public async Task<int> MoveAsync(IReadOnlyList<MessageItemViewModel> messages, NavItemViewModel target)
    {
        if (target.Folder is not { } destination)
        {
            return 0;
        }

        var moved = 0;
        foreach (var (account, folder, items) in Groups(messages.Where(m => m.AccountId == target.AccountId && m.Folder != destination.FullName)))
        {
            try
            {
                await _mail.MoveAsync(account, folder, Uids(items), destination.FullName);
            }
            catch (MailConnectionException ex)
            {
                ShowError(account, ex);
                continue;
            }

            Removed(items);
            if (items.Count(m => !m.IsRead) is var unread and > 0)
            {
                AdjustUnread(account.Id, destination.FullName, unread);
            }

            moved += items.Count;
        }

        return moved;
    }

    /// <summary>
    /// Marque lus (ou non lus) plusieurs mails d'un coup (clic droit sur une sélection) : une commande par dossier,
    /// puis les lignes, le cache, les compteurs et le mail ouvert suivent.
    /// </summary>
    /// <returns>Le nombre de mails changés (ceux qui l'étaient déjà ne sont pas redemandés au serveur).</returns>
    public async Task<int> SetReadAsync(IReadOnlyList<MessageItemViewModel> messages, bool read)
    {
        var changed = 0;
        foreach (var (account, folder, items) in Groups(messages.Where(m => m.IsRead != read)))
        {
            try
            {
                await _mail.SetFlagsAsync(account, folder, Uids(items), seen: read, flagged: null);
            }
            catch (MailConnectionException ex)
            {
                ShowError(account, ex);
                continue;
            }

            var headers = items.Select(m => m.Header with { IsRead = read }).ToList();
            var delta = Messages.UpdateHeaders(account.Id, folder, headers).Count(h => h.IsRead != read);
            if (delta > 0)
            {
                AdjustUnread(account.Id, folder, read ? -delta : delta);
            }

            foreach (var header in headers)
            {
                Reader.SyncHeader(account.Id, folder, header);
            }

            changed += items.Count;
        }

        return changed;
    }

    /// <summary>Mails groupés par compte et dossier (une commande au serveur par groupe) ; ceux d'un compte retiré sont écartés.</summary>
    private List<(Account Account, string Folder, List<MessageItemViewModel> Items)> Groups(IEnumerable<MessageItemViewModel> messages) =>
        [.. messages.Distinct()
            .GroupBy(m => (m.AccountId, m.Folder))
            .Select(g => (Account: _accounts.Find(g.Key.AccountId), g.Key.Folder, Items: g.ToList()))
            .Where(g => g.Account is not null)
            .Select(g => (g.Account!, g.Folder, g.Items))];

    private static List<uint> Uids(List<MessageItemViewModel> items) => [.. items.Select(m => m.Uid)];

    private void ShowError(Account account, MailConnectionException ex) =>
        ErrorMessage = _strings.Format("Mailbox_AccountError", account.Title, MailErrors.Describe(_strings, ex));

    /// <summary>Mails partis de leur dossier : volet de lecture, compteurs, liste et cache.</summary>
    private void Removed(IReadOnlyList<MessageItemViewModel> items)
    {
        if (Reader.HasMessage && items.Any(m => Reader.Uid == m.Uid && Reader.Folder == m.Folder && Reader.Account?.Id == m.AccountId))
        {
            Reader.Clear();
            _shell.OpenMessageId = null;
        }

        foreach (var group in items.Where(m => !m.IsRead).GroupBy(m => (m.AccountId, m.Folder)))
        {
            AdjustUnread(group.Key.AccountId, group.Key.Folder, -group.Count());
        }

        Messages.Remove(items);
    }

    private bool CanRefresh() => !IsRefreshing;

    private NavItemViewModel? InboxOf(string accountId) =>
        _all.FirstOrDefault(i => i.AccountId == accountId && i.Role == FolderRole.Inbox && i.CanSelect)
        ?? _all.FirstOrDefault(i => i.AccountId == accountId && i.CanSelect);

    private void SelectInitial()
    {
        var restored = _all.FirstOrDefault(i =>
            i.CanSelect && i.AccountId == _shell.SelectedAccountId && i.Folder!.FullName == _shell.SelectedFolderPath);
        var first = _all.FirstOrDefault(i => i.IsAccount);
        SelectedItem = restored ?? _all.FirstOrDefault(i => i.IsUnified) ?? (first is null ? null : InboxOf(first.AccountId));
    }

    /// <summary>
    /// Reconstruit la liste depuis les comptes et le cache. Le dossier choisi reste choisi, sans recharger ses mails ;
    /// les comptes repliés le restent.
    /// </summary>
    private void Rebuild()
    {
        var selected = SelectedItem is { Folder: { } current } ? (SelectedItem.AccountId, current.FullName) : default;
        var items = new List<NavItemViewModel>();
        var accounts = _accounts.All;
        if (accounts.Count > 1)
        {
            var unread = accounts.Sum(a => _cache.GetFolders(a.Id).FirstOrDefault(f => f.Role == FolderRole.Inbox)?.Unread ?? 0);
            items.Add(NavItemViewModel.ForUnified(_strings.Get("Folder_Unified"), unread));
        }

        foreach (var account in accounts)
        {
            var browser = account.Auth != AccountAuth.Password;
            var line = NavItemViewModel.ForAccount(account.Id, account.Title, browser);
            line.IsExpanded = !_collapsed.Contains(account.Id);
            items.Add(line);
            foreach (var folder in FolderOrder.Sort(_cache.GetFolders(account.Id)))
            {
                items.Add(NavItemViewModel.ForFolder(account.Id, folder, FolderTitle(folder), browser));
            }
        }

        _all = items;
        foreach (var account in accounts)
        {
            UpdateAccountUnread(account.Id);
        }

        _rebuilding = true;
        try
        {
            Items.Clear();
            foreach (var item in items.Where(i => i.IsAccount || !_collapsed.Contains(i.AccountId)))
            {
                Items.Add(item);
            }

            SelectedItem = items.FirstOrDefault(i => i.Folder is { } f && (i.AccountId, f.FullName) == selected);
        }
        finally
        {
            _rebuilding = false;
        }

        HasAccounts = accounts.Count > 0;
    }
}
