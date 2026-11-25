using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
        Messages.CollectionChanged += OnMessagesChanged;
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

    private IReadOnlyList<MessageItemViewModel> _selection = [];
    private bool _removing;

    /// <summary>
    /// Mails choisis dans la liste (clic, Ctrl+A, Maj ou Ctrl + clic). Un seul : c'est le mail ouvert ; plusieurs : le
    /// mail ouvert reste ouvert, et Suppr ou le clic droit agissent sur tous.
    /// </summary>
    public IReadOnlyList<MessageItemViewModel> SelectedMessages => _selection;

    /// <summary>
    /// Sélection de la liste changée (par la vue). Un seul mail choisi : il s'ouvre ; aucun : plus rien d'ouvert ;
    /// plusieurs : rien de neuf ne s'ouvre (Ctrl+A n'ouvre pas, donc ne marque pas lu, le premier mail venu).
    /// </summary>
    public void SetSelection(IEnumerable<MessageItemViewModel> selected)
    {
        _selection = [.. selected.Distinct()];

        // Pendant qu'une sélection quitte la liste, le dernier mail restant ne doit pas s'ouvrir (ni être marqué lu).
        if (_selection.Count <= 1 && !_removing)
        {
            SelectedMessage = _selection.Count == 1 ? _selection[0] : null;
        }
    }

    /// <summary>Mails visés par le clic droit sur <paramref name="clicked"/> : toute la sélection s'il en fait partie, sinon lui seul.</summary>
    public IReadOnlyList<MessageItemViewModel> TargetsOf(MessageItemViewModel clicked) =>
        _selection.Count > 1 && _selection.Contains(clicked) ? _selection : [clicked];

    /// <summary>Mail choisi par le programme (liste rafraîchie, mail rouvert après une mise à jour) : il devient la sélection.</summary>
    partial void OnSelectedMessageChanged(MessageItemViewModel? value)
    {
        if (value is not null && !_selection.Contains(value))
        {
            _selection = [value];
        }
        else if (value is null && _selection.Count == 1)
        {
            _selection = [];
        }
    }

    /// <summary>Mails retirés de la liste (ou liste vidée) : ils quittent aussi la sélection.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_selection.Count == 0)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _selection = [];
        }
        else if (e.OldItems is { Count: > 0 } removed)
        {
            _selection = [.. _selection.Where(m => !removed.Contains(m))];
        }
    }

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
        _unified = [];
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
        if (IsUnified)
        {
            var unifiedCancellation = _loading ?? Restart();
            IsLoading = true;
            try
            {
                foreach (var item in await NextUnifiedPageAsync(unifiedCancellation.Token))
                {
                    Messages.Add(item);
                }
            }
            catch (OperationCanceledException) when (unifiedCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                if (!unifiedCancellation.IsCancellationRequested)
                {
                    IsLoading = false;
                }
            }

            return;
        }

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
    public MessageHeader? UpdateHeader(string accountId, string folder, MessageHeader header) =>
        UpdateHeaders(accountId, folder, [header]) is [var previous] ? previous : null;

    /// <summary>
    /// Lu / non lu ou étoile changés sur plusieurs mails d'un même dossier (clic droit sur une sélection) : les lignes
    /// suivent, et le cache en une seule fois.
    /// </summary>
    /// <returns>Les en-têtes d'avant des mails trouvés dans la liste.</returns>
    public IReadOnlyList<MessageHeader> UpdateHeaders(string accountId, string folder, IReadOnlyList<MessageHeader> headers)
    {
        var rows = new Dictionary<uint, MessageItemViewModel>();
        foreach (var item in Messages.Where(m => m.AccountId == accountId && m.Folder == folder))
        {
            rows.TryAdd(item.Uid, item);
        }

        var previous = new List<MessageHeader>();
        var changed = new List<MessageHeader>();
        foreach (var header in headers)
        {
            if (rows.TryGetValue(header.Uid, out var item))
            {
                previous.Add(item.Header);
                item.Header = header;
                changed.Add(header);
            }
        }

        if (changed.Count > 0 && _account?.Id == accountId && _folder?.FullName == folder && _uidValidity != 0)
        {
            _cache.SaveMessages(accountId, folder, _uidValidity, changed);
        }

        return previous;
    }

    public MessageItemViewModel? Find(string accountId, string folder, uint uid) =>
        Messages.FirstOrDefault(m => m.Uid == uid && m.AccountId == accountId && m.Folder == folder);

    /// <summary>Comptes mêlés dans la boîte de réception unifiée (vide sinon).</summary>
    private List<Account> _unified = [];
    private readonly Dictionary<string, (Queue<MessageHeader> Buffer, int Skip, bool Done, uint Validity)> _sources = [];

    public bool IsUnified => _unified.Count > 0;

    /// <summary>
    /// Boîte de réception unifiée : les boîtes de réception de tous les comptes, mêlées du plus récent au plus ancien,
    /// par pages (chaque compte est lu au fur et à mesure).
    /// </summary>
    public async Task ShowUnifiedAsync(IReadOnlyList<Account> accounts, string title)
    {
        var cancellation = Restart();
        _account = null;
        _folder = null;
        _unified = [.. accounts];
        _sources.Clear();
        _folderTitle = title;
        IsSearching = false;
        SearchText = null;
        CanSearchServer = false;
        SelectedMessage = null;
        Title = title;
        HasFolder = true;
        ErrorMessage = null;
        HasMore = false;
        Messages.Clear();
        var now = _time.GetUtcNow();
        foreach (var (account, header) in accounts
            .SelectMany(a => _cache.GetMessages(a.Id, "INBOX", 0, PageSize).Select(h => (a, h)))
            .OrderByDescending(p => p.h.Date).Take(PageSize))
        {
            Messages.Add(new MessageItemViewModel(account.Id, "INBOX", header, _strings.Get("Draft_NoSubject"), now));
        }

        IsLoading = true;
        try
        {
            foreach (var account in accounts)
            {
                _sources[account.Id] = (new Queue<MessageHeader>(), 0, false, 0);
            }

            var page = await NextUnifiedPageAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Messages.Clear();
            foreach (var item in page)
            {
                Messages.Add(item);
            }
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

    /// <summary>Page suivante de la boîte unifiée : on complète la réserve de chaque compte, puis on prend les plus récents.</summary>
    private async Task<List<MessageItemViewModel>> NextUnifiedPageAsync(CancellationToken cancellationToken)
    {
        foreach (var account in _unified)
        {
            var source = _sources[account.Id];
            if (source.Done || source.Buffer.Count >= PageSize)
            {
                continue;
            }

            try
            {
                var page = await _mail.GetMessagesAsync(account, "INBOX", source.Skip, PageSize, cancellationToken);
                _cache.SaveMessages(account.Id, "INBOX", page.UidValidity, page.Messages);
                foreach (var header in page.Messages)
                {
                    source.Buffer.Enqueue(header);
                }

                var skip = source.Skip + page.Messages.Count;
                _sources[account.Id] = (source.Buffer, skip, page.Messages.Count < PageSize || skip >= page.Total, page.UidValidity);
            }
            catch (MailConnectionException ex)
            {
                // Un compte injoignable n'empêche pas les autres de s'afficher.
                ErrorMessage = MailErrors.Describe(_strings, ex);
                _sources[account.Id] = (source.Buffer, source.Skip, true, source.Validity);
            }
        }

        var now = _time.GetUtcNow();
        var taken = new List<MessageItemViewModel>();
        while (taken.Count < PageSize)
        {
            var next = _unified.Where(a => _sources[a.Id].Buffer.Count > 0).OrderByDescending(a => _sources[a.Id].Buffer.Peek().Date).FirstOrDefault();
            if (next is null)
            {
                break;
            }

            taken.Add(new MessageItemViewModel(next.Id, "INBOX", _sources[next.Id].Buffer.Dequeue(), _strings.Get("Draft_NoSubject"), now));
        }

        HasMore = _unified.Any(a => _sources[a.Id].Buffer.Count > 0 || !_sources[a.Id].Done);
        return taken;
    }

    /// <summary>
    /// Nouveaux mails arrivés (réception en direct) : ajoutés en tête si cette boîte de réception est affichée.
    /// </summary>
    public void InsertNewest(string accountId, string folder, IReadOnlyList<MessageHeader> headers)
    {
        var shown = !IsSearching
            && ((_account?.Id == accountId && _folder?.FullName == folder) || (folder == "INBOX" && _unified.Any(a => a.Id == accountId)));
        if (!shown)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var header in headers.OrderBy(h => h.Date))
        {
            if (Find(accountId, folder, header.Uid) is null)
            {
                Messages.Insert(0, new MessageItemViewModel(accountId, folder, header, _strings.Get("Draft_NoSubject"), now));
                _total++;
            }
        }

        if (_account?.Id == accountId && _uidValidity != 0)
        {
            _cache.SaveMessages(accountId, folder, _uidValidity, headers);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Mail déplacé ou supprimé : retiré de la liste (et du cache).</summary>
    public void Remove(MessageItemViewModel item) => Remove([item]);

    /// <summary>Mails déplacés ou supprimés (une sélection) : retirés de la liste, et du cache une fois par dossier.</summary>
    public void Remove(IReadOnlyList<MessageItemViewModel> items)
    {
        _removing = true;
        try
        {
            if (SelectedMessage is { } selected && items.Contains(selected))
            {
                SelectedMessage = null;
            }

            foreach (var item in items)
            {
                if (Messages.Remove(item) && _total > 0)
                {
                    _total--;
                }
            }
        }
        finally
        {
            _removing = false;
        }

        foreach (var group in items.GroupBy(m => (m.AccountId, m.Folder)))
        {
            _cache.RemoveMessages(group.Key.AccountId, group.Key.Folder, [.. group.Select(m => m.Uid)]);
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
        else if (_unified.Count > 0)
        {
            await ShowUnifiedAsync(_unified, _folderTitle ?? string.Empty);
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
        _unified = [];
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
