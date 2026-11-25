using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Cache;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// Liste des mails du dossier choisi : en-têtes seulement, par pages de <see cref="PageSize"/>, du plus récent au plus
/// ancien. Le cache s'affiche tout de suite, puis le serveur le remplace.
/// </summary>
public sealed partial class MessageListViewModel : ObservableObject
{
    public const int PageSize = 50;

    private readonly IMailService _mail;
    private readonly MailCache _cache;
    private readonly IStrings _strings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private CancellationTokenSource? _loading;
    private Account? _account;
    private MailFolderInfo? _folder;
    private int _total;
    private uint _uidValidity;
    private string? _folderTitle;

    public MessageListViewModel(IMailService mail, MailCache cache, IStrings strings, TimeProvider? time = null, ILogger<MessageListViewModel>? logger = null)
    {
        _mail = mail;
        _cache = cache;
        _strings = strings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        Title = strings.Get("Pane_Messages");
    }

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Dossier choisi, chargé, sans aucun mail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasFolder { get; set; }

    public bool IsEmpty => HasFolder && !IsLoading && Messages.Count == 0;

    /// <summary>Mail choisi dans la liste (ouvert dans le volet de lecture).</summary>
    [ObservableProperty]
    public partial MessageItemViewModel? SelectedMessage { get; set; }

    public string? AccountId => _account?.Id;

    public string? FolderName => _folder?.FullName;

    /// <summary>Affiche un dossier : le cache d'abord, puis la première page du serveur.</summary>
    public async Task ShowAsync(Account account, MailFolderInfo folder, string title)
    {
        var cancellation = Restart();
        if (_account?.Id != account.Id || _folder?.FullName != folder.FullName)
        {
            // Autre dossier : rien de choisi (un même UID n'y désigne pas le même mail).
            SelectedMessage = null;
        }

        _account = account;
        _folder = folder;
        _folderTitle = title;
        _total = 0;
        IsSearching = false;
        SearchText = null;
        CanSearchServer = false;
        Title = title;
        HasFolder = true;
        ErrorMessage = null;
        HasMore = false;
        Replace(_cache.GetMessages(account.Id, folder.FullName, 0, PageSize));

        IsLoading = true;
        try
        {
            var page = await _mail.GetMessagesAsync(account, folder.FullName, 0, PageSize, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _total = page.Total;
            _uidValidity = page.UidValidity;
            _cache.SaveMessages(account.Id, folder.FullName, page.UidValidity, page.Messages);
            Replace(page.Messages);
            HasMore = page.Total > Messages.Count;
        }
        catch (MailConnectionException ex) when (!cancellation.IsCancellationRequested)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
            HasMore = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Un autre dossier a été choisi entre-temps.
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Page suivante (mails plus anciens).</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync()
    {
        if (_account is not { } account || _folder is not { } folder)
        {
            return;
        }

        var cancellation = _loading ?? Restart();
        IsLoading = true;
        try
        {
            var page = await _mail.GetMessagesAsync(account, folder.FullName, Messages.Count, PageSize, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _total = page.Total;
            _uidValidity = page.UidValidity;
            _cache.SaveMessages(account.Id, folder.FullName, page.UidValidity, page.Messages);
            var known = Messages.Select(m => m.Uid).ToHashSet();
            var now = _time.GetUtcNow();
            foreach (var header in page.Messages.Where(h => known.Add(h.Uid)))
            {
                Messages.Add(new MessageItemViewModel(account.Id, folder.FullName, header, _strings.Get("Draft_NoSubject"), now));
            }

            HasMore = page.Messages.Count > 0 && _total > Messages.Count;
        }
        catch (MailConnectionException ex) when (!cancellation.IsCancellationRequested)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Lu / non lu ou étoile changé à la lecture : la ligne et le cache suivent.
    /// </summary>
    /// <returns>L'en-tête d'avant, ou <c>null</c> si ce mail n'est pas dans la liste.</returns>
    public MessageHeader? UpdateHeader(string accountId, string folder, MessageHeader header)
    {
        if (Find(accountId, folder, header.Uid) is not { } item)
        {
            return null;
        }

        var previous = item.Header;
        item.Header = header;
        if (_account?.Id == accountId && _folder?.FullName == folder && _uidValidity != 0)
        {
            _cache.SaveMessages(accountId, folder, _uidValidity, [header]);
        }

        return previous;
    }

    public MessageItemViewModel? Find(string accountId, string folder, uint uid) =>
        Messages.FirstOrDefault(m => m.Uid == uid && m.AccountId == accountId && m.Folder == folder);

    /// <summary>Mail déplacé ou supprimé : retiré de la liste (et du cache).</summary>
    public void Remove(MessageItemViewModel item)
    {
        if (SelectedMessage == item)
        {
            SelectedMessage = null;
        }

        Messages.Remove(item);
        _cache.RemoveMessage(item.AccountId, item.Folder, item.Uid);
        if (_total > 0)
        {
            _total--;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Recherche en cours (résultats affichés à la place du dossier).</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>Texte cherché.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>La recherche peut aussi être faite sur le serveur, dans le dossier d'où elle est partie.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchServerCommand))]
    public partial bool CanSearchServer { get; set; }

    /// <summary>
    /// Recherche plein texte dans le cache (tous comptes et dossiers : en-têtes, et texte des mails déjà lus), sans
    /// connexion. Un texte vide revient au dossier.
    /// </summary>
    public async Task SearchAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await EndSearchAsync();
            return;
        }

        Restart();
        SearchText = text.Trim();
        IsSearching = true;
        HasMore = false;
        ErrorMessage = null;
        Title = _strings.Format("Search_Title", SearchText);
        Messages.Clear();
        SelectedMessage = null;
        var now = _time.GetUtcNow();
        foreach (var hit in _cache.Search(SearchText, 200))
        {
            Messages.Add(new MessageItemViewModel(hit.AccountId, hit.Folder, hit.Header, _strings.Get("Draft_NoSubject"), now));
        }

        HasFolder = true;
        CanSearchServer = _account is not null && _folder is not null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Même recherche sur le serveur, dans le dossier d'où elle est partie (texte complet de tous les mails).</summary>
    [RelayCommand(CanExecute = nameof(CanSearchServer))]
    public async Task SearchServerAsync()
    {
        if (_account is not { } account || _folder is not { } folder || SearchText is not { } text)
        {
            return;
        }

        var cancellation = Restart();
        IsLoading = true;
        try
        {
            var found = await _mail.SearchAsync(account, folder.FullName, text, 200, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var now = _time.GetUtcNow();
            foreach (var header in found.Where(h => Find(account.Id, folder.FullName, h.Uid) is null))
            {
                Messages.Add(new MessageItemViewModel(account.Id, folder.FullName, header, _strings.Get("Draft_NoSubject"), now));
            }

            CanSearchServer = false;
        }
        catch (MailConnectionException ex) when (!cancellation.IsCancellationRequested)
        {
            ErrorMessage = MailErrors.Describe(_strings, ex);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Fin de la recherche : retour au dossier.</summary>
    public async Task EndSearchAsync()
    {
        if (!IsSearching)
        {
            return;
        }

        IsSearching = false;
        SearchText = null;
        CanSearchServer = false;
        if (_account is { } account && _folder is { } folder)
        {
            await ShowAsync(account, folder, _folderTitle ?? folder.Name);
        }
        else
        {
            Clear();
        }
    }

    /// <summary>Plus aucun dossier choisi (compte supprimé…).</summary>
    public void Clear()
    {
        Restart();
        _account = null;
        _folder = null;
        SelectedMessage = null;
        Messages.Clear();
        Title = _strings.Get("Pane_Messages");
        HasFolder = false;
        HasMore = false;
        IsLoading = false;
        ErrorMessage = null;
    }

    private bool CanLoadMore() => HasMore && !IsLoading;

    private CancellationTokenSource Restart()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        return _loading;
    }

    private void Replace(IReadOnlyList<MessageHeader> headers)
    {
        var selected = SelectedMessage?.Uid;
        Messages.Clear();
        var now = _time.GetUtcNow();
        var noSubject = _strings.Get("Draft_NoSubject");
        foreach (var header in headers)
        {
            Messages.Add(new MessageItemViewModel(_account!.Id, _folder!.FullName, header, noSubject, now));
        }

        // Liste rafraîchie (cache puis serveur) : le mail ouvert reste choisi.
        SelectedMessage = selected is { } uid ? Find(_account!.Id, _folder!.FullName, uid) : null;
        OnPropertyChanged(nameof(IsEmpty));
    }
}
