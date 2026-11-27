using System.Collections.Concurrent;
using System.Net.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Accounts.OAuth;

namespace QuaiMail.Core.Mail;

/// <summary>Nouveaux mails arrivés dans la boîte de réception d'un compte.</summary>
public sealed class NewMailEventArgs(Account account, IReadOnlyList<MessageHeader> messages) : EventArgs
{
    public Account Account { get; } = account;

    /// <summary>Du plus récent au plus ancien.</summary>
    public IReadOnlyList<MessageHeader> Messages { get; } = messages;
}

/// <summary>Réception en direct : surveille la boîte de réception de chaque compte.</summary>
public interface IMailWatcher : IAsyncDisposable
{
    /// <summary>Déclenché depuis un fil de fond : l'interface le ramène sur son fil.</summary>
    event EventHandler<NewMailEventArgs>? NewMail;

    /// <summary>Surveille exactement ces comptes (les autres sont arrêtés).</summary>
    void Watch(IReadOnlyList<Account> accounts);

    /// <summary>
    /// Vérifie tout de suite chaque boîte de réception, sans attendre la fin de l'IDLE ni de l'attente de reconnexion
    /// (sortie de veille de l'ordinateur : les connexions sont souvent mortes sans le savoir). Les mails arrivés entre-temps
    /// sont annoncés, comme après une coupure.
    /// </summary>
    void Wake();
}

/// <summary>
/// Réception en direct par MailKit : une connexion à part par compte, boîte de réception ouverte en lecture seule,
/// IDLE quand le serveur le permet (sinon NOOP toutes les deux minutes). Les mails arrivés pendant une coupure sont
/// annoncés à la reconnexion (UID plus grand que le dernier vu). Reconnexion automatique, avec une attente croissante
/// (5 s, 15 s, 1 min, puis 5 min). Mêmes règles que <see cref="MailKitMailService"/> : TLS vérifié, journaux sans contenu.
/// </summary>
public sealed class MailKitMailWatcher : IMailWatcher
{
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly ICredentialStore _credentials;
    private readonly AccessTokens? _tokens;
    private readonly ILogger _logger;
    private readonly RemoteCertificateValidationCallback _certificateValidation;
    private readonly ConcurrentDictionary<string, (Account Account, CancellationTokenSource Stop, Task Loop)> _watchers = new();

    // Remplacé à chaque Wake (l'ancien, annulé, n'est jamais libéré : il n'a ni minuterie ni ressource).
    private CancellationTokenSource _wake = new();

    public MailKitMailWatcher(ICredentialStore credentials, ILogger<MailKitMailWatcher>? logger = null, AccessTokens? tokens = null)
        : this(credentials, logger, null, tokens)
    {
    }

    internal MailKitMailWatcher(ICredentialStore credentials, ILogger<MailKitMailWatcher>? logger, RemoteCertificateValidationCallback? certificateValidation, AccessTokens? tokens = null)
    {
        _credentials = credentials;
        _tokens = tokens;
        _logger = logger ?? NullLogger<MailKitMailWatcher>.Instance;
        _certificateValidation = certificateValidation ?? MailKitMailService.StrictValidation;
    }

    public event EventHandler<NewMailEventArgs>? NewMail;

    /// <summary>Durée maximale d'un IDLE avant de le relancer (les serveurs coupent vers 30 min).</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(9);

    /// <summary>Sans IDLE : intervalle entre deux NOOP.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(2);

    internal TimeSpan[] Backoff { get; init; } = DefaultBackoff;

    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Nombre de connexions réussies (tests : reconnexion automatique).</summary>
    internal int Connections;

    public void Watch(IReadOnlyList<Account> accounts)
    {
        foreach (var (id, watcher) in _watchers)
        {
            if (accounts.All(a => a.Id != id) || accounts.First(a => a.Id == id) != watcher.Account)
            {
                if (_watchers.TryRemove(id, out var removed))
                {
                    removed.Stop.Cancel();
                }
            }
        }

        foreach (var account in accounts.Where(a => !_watchers.ContainsKey(a.Id)))
        {
            var stop = new CancellationTokenSource();
            _watchers[account.Id] = (account, stop, Task.Run(() => RunAsync(account, stop.Token)));
        }
    }

    public void Wake() => Interlocked.Exchange(ref _wake, new CancellationTokenSource()).Cancel();

    public async ValueTask DisposeAsync()
    {
        var loops = _watchers.Values.ToList();
        _watchers.Clear();
        foreach (var watcher in loops)
        {
            await watcher.Stop.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(loops.Select(w => w.Loop.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        foreach (var watcher in loops)
        {
            watcher.Stop.Dispose();
        }
    }

    private async Task RunAsync(Account account, CancellationToken stop)
    {
        var attempt = 0;
        uint? lastSeen = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var password = account.Auth == AccountAuth.Password
                    ? _credentials.GetPassword(account.Id)
                    : _tokens is null ? null : await _tokens.GetAsync(account, stop).ConfigureAwait(false);
                if (password is null)
                {
                    return;
                }

                using var client = new ImapClient
                {
                    Timeout = (int)Timeout.TotalMilliseconds,
                    ServerCertificateValidationCallback = _certificateValidation,
                    CheckCertificateRevocation = true,
                };
                var options = account.Imap.Security == MailSecurity.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
                await client.ConnectAsync(account.Imap.Host, account.Imap.Port, options, stop).ConfigureAwait(false);
                if (account.Auth == AccountAuth.Password)
                {
                    await client.AuthenticateAsync(account.UserName, password, stop).ConfigureAwait(false);
                }
                else
                {
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(account.UserName, password), stop).ConfigureAwait(false);
                }
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, stop).ConfigureAwait(false);
                Interlocked.Increment(ref Connections);
                attempt = 0;

                // Premier contact : rien n'est « nouveau ». Reconnexion : ce qui est arrivé pendant la coupure l'est.
                lastSeen ??= inbox.UidNext is { } next && next.Id > 0 ? next.Id - 1 : await HighestUidAsync(inbox, stop).ConfigureAwait(false);
                lastSeen = await AnnounceAsync(account, inbox, lastSeen.Value, stop).ConfigureAwait(false);

                while (!stop.IsCancellationRequested && client.IsConnected)
                {
                    using var changed = CancellationTokenSource.CreateLinkedTokenSource(Volatile.Read(ref _wake).Token);
                    changed.CancelAfter(IdleTimeout);
                    void OnCount(object? sender, EventArgs e) => changed.Cancel();
                    inbox.CountChanged += OnCount;
                    try
                    {
                        if (client.Capabilities.Contains(ImapCapability.Idle))
                        {
                            await client.IdleAsync(changed.Token, stop).ConfigureAwait(false);
                        }
                        else
                        {
                            using var wait = CancellationTokenSource.CreateLinkedTokenSource(changed.Token, stop);
                            try
                            {
                                await Task.Delay(PollInterval, wait.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                            {
                            }

                            await client.NoOpAsync(stop).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        inbox.CountChanged -= OnCount;
                    }

                    lastSeen = await AnnounceAsync(account, inbox, lastSeen.Value, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var error = MailKitMailService.Classify(ex);
                _logger.LogInformation("Réception en direct {Server} interrompue ({Error}, {Type}) ; reconnexion automatique", MailKitMailService.Describe(account.Imap), error?.ToString() ?? "inconnue", ex.GetType().Name);
                if (error is MailError.Authentication or MailError.Certificate or MailError.SignInExpired || ex is OAuthException { Error: not OAuthError.Network })
                {
                    // Mot de passe, autorisation ou certificat refusé : inutile d'insister (l'erreur est montrée à l'ouverture
                    // du compte). La surveillance reprendra au prochain Watch (compte reconnecté, par exemple).
                    if (_watchers.TryGetValue(account.Id, out var current) && current.Stop.Token == stop)
                    {
                        _watchers.TryRemove(account.Id, out _);
                    }

                    return;
                }
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop, Volatile.Read(ref _wake).Token);
                await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                // Réveillé : nouvelle tentative tout de suite.
            }
            catch (OperationCanceledException)
            {
                return;
            }

            attempt++;
        }
    }

    /// <summary>Annonce les mails d'UID supérieur au dernier vu ; retourne le nouveau dernier UID vu.</summary>
    private async Task<uint> AnnounceAsync(Account account, IMailFolder inbox, uint lastSeen, CancellationToken stop)
    {
        var range = new UniqueIdRange(new UniqueId(lastSeen + 1), UniqueId.MaxValue);
        var uids = (await inbox.SearchAsync(SearchQuery.Uids(range), stop).ConfigureAwait(false)).Where(u => u.Id > lastSeen).ToList();
        if (uids.Count == 0)
        {
            return lastSeen;
        }

        var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size);
        var summaries = await inbox.FetchAsync(uids, request, stop).ConfigureAwait(false);
        var headers = summaries.Select(MailKitMailService.ToHeader).OrderByDescending(h => h.Uid).ToList();
        _logger.LogInformation("{Count} nouveau(x) mail(s) sur {Server}", headers.Count, MailKitMailService.Describe(account.Imap));
        NewMail?.Invoke(this, new NewMailEventArgs(account, headers));
        return uids.Max(u => u.Id);
    }

    private static async Task<uint> HighestUidAsync(IMailFolder inbox, CancellationToken stop)
    {
        if (inbox.Count == 0)
        {
            return 0;
        }

        var last = await inbox.FetchAsync(inbox.Count - 1, inbox.Count - 1, new FetchRequest(MessageSummaryItems.UniqueId), stop).ConfigureAwait(false);
        return last.Count > 0 ? last[0].UniqueId.Id : 0;
    }
}
