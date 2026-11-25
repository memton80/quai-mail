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

    public string? AccountId => _account?.Id;

    public string? FolderName => _folder?.FullName;

    /// <summary>Affiche un dossier : le cache d'abord, puis la première page du serveur.</summary>
    public async Task ShowAsync(Account account, MailFolderInfo folder, string title)
    {
        var cancellation = Restart();
        _account = account;
        _folder = folder;
        _total = 0;
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
            _cache.SaveMessages(account.Id, folder.FullName, page.UidValidity, page.Messages);
            var known = Messages.Select(m => m.Uid).ToHashSet();
            var now = _time.GetUtcNow();
            foreach (var header in page.Messages.Where(h => known.Add(h.Uid)))
            {
                Messages.Add(new MessageItemViewModel(header, _strings.Get("Draft_NoSubject"), now));
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

    /// <summary>Plus aucun dossier choisi (compte supprimé…).</summary>
    public void Clear()
    {
        Restart();
        _account = null;
        _folder = null;
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
        Messages.Clear();
        var now = _time.GetUtcNow();
        var noSubject = _strings.Get("Draft_NoSubject");
        foreach (var header in headers)
        {
            Messages.Add(new MessageItemViewModel(header, noSubject, now));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
