using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Services;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Volet de gauche : les comptes et leurs dossiers. Les dossiers en cache s'affichent tout de suite, puis chaque compte
/// est relu sur son serveur. Choisir un dossier affiche ses mails (<see cref="MessageListViewModel"/>) et le note dans
/// l'état de la fenêtre (<see cref="ShellViewModel"/>).
/// </summary>
public sealed partial class MailboxViewModel : ObservableObject
{
    private readonly AccountStore _accounts;
    private readonly ICredentialStore _credentials;
    private readonly IMailService _mail;
    private readonly MailCache _cache;
    private readonly ShellViewModel _shell;
    private readonly IDialogService _dialogs;
    private readonly IStrings _strings;
    private readonly ILogger _logger;
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
        ILogger<MailboxViewModel>? logger = null)
    {
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

    public ObservableCollection<NavItemViewModel> Items { get; } = [];

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

    /// <summary>Compte tout juste ajouté : affiché et sa boîte de réception choisie.</summary>
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
        _cache.RemoveAccount(accountId);
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
        if (shown != default && Items.FirstOrDefault(i => i.Folder is { } f && (i.AccountId, f.FullName) == shown) is { } item
            && _accounts.Find(item.AccountId) is { } owner)
        {
            await ShowFolderAsync(owner, item.Folder!, item.Title);
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
        if (Items.FirstOrDefault(i => i.AccountId == accountId && i.Folder?.FullName == folder) is { } item)
        {
            item.Unread = Math.Max(0, item.Unread + delta);
        }
    }

    /// <summary>Dossiers vers lesquels un mail peut être déplacé (même compte, sauf son dossier).</summary>
    public IReadOnlyList<NavItemViewModel> MoveTargets(MessageItemViewModel message) =>
        [.. Items.Where(i => i.AccountId == message.AccountId && i.CanSelect && i.Folder!.FullName != message.Folder)];

    /// <summary>
    /// Supprimer : vers la corbeille du serveur ; s'il n'y en a pas, ou si le mail y est déjà, suppression définitive
    /// seulement après confirmation (« Annuler » par défaut).
    /// </summary>
    /// <returns><c>true</c> si le mail a quitté le dossier.</returns>
    public async Task<bool> DeleteAsync(MessageItemViewModel message)
    {
        if (_accounts.Find(message.AccountId) is not { } account)
        {
            return false;
        }

        try
        {
            if (!await _mail.MoveToTrashAsync(account, message.Folder, message.Uid))
            {
                var confirmed = await _dialogs.ConfirmAsync(
                    _strings.Get("DeleteForever_Title"),
                    _strings.Format("DeleteForever_Message", message.Subject),
                    _strings.Get("DeleteForever_Confirm"),
                    isDestructive: true);
                if (!confirmed)
                {
                    return false;
                }

                await _mail.DeletePermanentlyAsync(account, message.Folder, message.Uid);
            }
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = _strings.Format("Mailbox_AccountError", account.Title, MailErrors.Describe(_strings, ex));
            return false;
        }

        Removed(message);
        return true;
    }

    /// <summary>Déplace un mail vers un autre dossier de son compte.</summary>
    public async Task<bool> MoveAsync(MessageItemViewModel message, NavItemViewModel target)
    {
        if (_accounts.Find(message.AccountId) is not { } account || target.Folder is not { } folder || target.AccountId != account.Id)
        {
            return false;
        }

        try
        {
            await _mail.MoveAsync(account, message.Folder, message.Uid, folder.FullName);
        }
        catch (MailConnectionException ex)
        {
            ErrorMessage = _strings.Format("Mailbox_AccountError", account.Title, MailErrors.Describe(_strings, ex));
            return false;
        }

        Removed(message);
        if (!message.IsRead)
        {
            target.Unread++;
        }

        return true;
    }

    private void Removed(MessageItemViewModel message)
    {
        if (Reader.HasMessage && Reader.Uid == message.Uid && Reader.Folder == message.Folder && Reader.Account?.Id == message.AccountId)
        {
            Reader.Clear();
            _shell.OpenMessageId = null;
        }

        if (!message.IsRead)
        {
            AdjustUnread(message.AccountId, message.Folder, -1);
        }

        Messages.Remove(message);
    }

    private bool CanRefresh() => !IsRefreshing;

    private NavItemViewModel? InboxOf(string accountId) =>
        Items.FirstOrDefault(i => i.AccountId == accountId && i.Role == FolderRole.Inbox && i.CanSelect)
        ?? Items.FirstOrDefault(i => i.AccountId == accountId && i.CanSelect);

    private void SelectInitial()
    {
        var restored = Items.FirstOrDefault(i =>
            i.CanSelect && i.AccountId == _shell.SelectedAccountId && i.Folder!.FullName == _shell.SelectedFolderPath);
        var first = Items.FirstOrDefault(i => i.IsAccount);
        SelectedItem = restored ?? (first is null ? null : InboxOf(first.AccountId));
    }

    /// <summary>
    /// Reconstruit la liste depuis les comptes et le cache. Le dossier choisi reste choisi, sans recharger ses mails.
    /// </summary>
    private void Rebuild()
    {
        var selected = SelectedItem is { Folder: { } current } ? (SelectedItem.AccountId, current.FullName) : default;
        var items = new List<NavItemViewModel>();
        foreach (var account in _accounts.All)
        {
            items.Add(NavItemViewModel.ForAccount(account.Id, account.Title));
            foreach (var folder in FolderOrder.Sort(_cache.GetFolders(account.Id)))
            {
                items.Add(NavItemViewModel.ForFolder(account.Id, folder, FolderTitle(folder)));
            }
        }

        _rebuilding = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            SelectedItem = items.FirstOrDefault(i => i.Folder is { } f && (i.AccountId, f.FullName) == selected);
        }
        finally
        {
            _rebuilding = false;
        }

        HasAccounts = items.Count > 0;
    }
}
