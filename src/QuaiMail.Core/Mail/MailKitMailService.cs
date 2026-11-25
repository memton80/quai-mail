using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Reading;

namespace QuaiMail.Core.Mail;

/// <summary>
/// IMAP et SMTP par MailKit. Une connexion IMAP est gardée ouverte par compte (une opération à la fois). Certificats
/// TLS toujours vérifiés par Windows (chaîne, nom, révocation) : aucune exception silencieuse. Journaux : serveur et
/// type d'erreur seulement, jamais d'identifiant, de mot de passe, d'objet ni d'adresse de correspondant.
/// </summary>
public sealed class MailKitMailService : IMailService, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ICredentialStore _credentials;
    private readonly ILogger _logger;
    private readonly RemoteCertificateValidationCallback _certificateValidation;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public MailKitMailService(ICredentialStore credentials, ILogger<MailKitMailService>? logger = null)
        : this(credentials, logger, null)
    {
    }

    /// <param name="certificateValidation">
    /// Tests seulement (certificat de test épinglé) ; par défaut, un certificat n'est accepté que si Windows ne lui
    /// trouve aucun défaut.
    /// </param>
    internal MailKitMailService(ICredentialStore credentials, ILogger<MailKitMailService>? logger, RemoteCertificateValidationCallback? certificateValidation)
    {
        _credentials = credentials;
        _logger = logger ?? NullLogger<MailKitMailService>.Instance;
        _certificateValidation = certificateValidation ?? StrictValidation;
    }

    internal TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Validation stricte : aucune erreur de chaîne, de nom ou de révocation.</summary>
    public static bool StrictValidation(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None;

    public async Task VerifyAsync(Account account, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        using (var imap = CreateImap())
        {
            await ConnectAsync(imap, account.Imap, "IMAP", account.UserName, password, cancellationToken).ConfigureAwait(false);
            await imap.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }

        using (var smtp = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds, ServerCertificateValidationCallback = _certificateValidation, CheckCertificateRevocation = true })
        {
            await ConnectAsync(smtp, account.Smtp, "SMTP", account.UserName, password, cancellationToken).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Réglages vérifiés (IMAP {Imap}, SMTP {Smtp})", Describe(account.Imap), Describe(account.Smtp));
    }

    public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(Account account, CancellationToken cancellationToken = default) =>
        WithImapAsync(account, async (client, ct) =>
        {
            var folders = new List<MailFolderInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var namespaces = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces : [new FolderNamespace('/', string.Empty)];
            foreach (var ns in namespaces)
            {
                var items = await client.GetFoldersAsync(ns, StatusItems.Unread | StatusItems.Count, subscribedOnly: false, ct).ConfigureAwait(false);
                foreach (var folder in items)
                {
                    if (seen.Add(folder.FullName) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent))
                    {
                        folders.Add(ToInfo(folder, ns));
                    }
                }
            }

            // La boîte de réception n'est pas toujours listée dans l'espace personnel.
            if (client.Inbox is { } inbox && seen.Add(inbox.FullName))
            {
                await inbox.StatusAsync(StatusItems.Unread | StatusItems.Count, ct).ConfigureAwait(false);
                folders.Add(ToInfo(inbox, null));
            }

            return (IReadOnlyList<MailFolderInfo>)FolderOrder.Sort(folders);
        }, cancellationToken);

    public Task<MessagePage> GetMessagesAsync(Account account, string folder, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        return WithImapAsync(account, async (client, ct) =>
        {
            var mailFolder = await OpenAsync(client, folder, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            try
            {
                var count = mailFolder.Count;
                var last = count - 1 - skip;
                if (last < 0)
                {
                    return new MessagePage(mailFolder.UidValidity, count, []);
                }

                var first = Math.Max(0, last - take + 1);
                var request = new FetchRequest(
                    MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                    | MessageSummaryItems.InternalDate | MessageSummaryItems.Size);
                var summaries = await mailFolder.FetchAsync(first, last, request, ct).ConfigureAwait(false);
                var headers = summaries
                    .OrderByDescending(s => s.Index)
                    .Select(ToHeader)
                    .ToList();
                return new MessagePage(mailFolder.UidValidity, count, headers);
            }
            finally
            {
                await CloseAsync(client, mailFolder).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public Task<MessageContent> GetMessageAsync(Account account, string folder, uint uid, CancellationToken cancellationToken = default) =>
        WithImapAsync(account, async (client, ct) =>
        {
            // Lecture seule : BODY.PEEK[], le mail n'est pas marqué lu par la lecture elle-même.
            var mailFolder = await OpenAsync(client, folder, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            try
            {
                var message = await mailFolder.GetMessageAsync(new UniqueId(uid), ct).ConfigureAwait(false);
                return MessageContent.FromMime(uid, message);
            }
            finally
            {
                await CloseAsync(client, mailFolder).ConfigureAwait(false);
            }
        }, cancellationToken);

    public Task SetFlagsAsync(Account account, string folder, uint uid, bool? seen, bool? flagged, CancellationToken cancellationToken = default) =>
        WithImapAsync(account, async (client, ct) =>
        {
            var add = (seen == true ? MessageFlags.Seen : MessageFlags.None) | (flagged == true ? MessageFlags.Flagged : MessageFlags.None);
            var remove = (seen == false ? MessageFlags.Seen : MessageFlags.None) | (flagged == false ? MessageFlags.Flagged : MessageFlags.None);
            if (add == MessageFlags.None && remove == MessageFlags.None)
            {
                return true;
            }

            var mailFolder = await OpenAsync(client, folder, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            try
            {
                var id = new UniqueId(uid);
                if (add != MessageFlags.None)
                {
                    await mailFolder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, add), ct).ConfigureAwait(false);
                }

                if (remove != MessageFlags.None)
                {
                    await mailFolder.StoreAsync(id, new StoreFlagsRequest(StoreAction.Remove, remove), ct).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                // Fermeture sans EXPUNGE : rien n'est jamais effacé au passage.
                await CloseAsync(client, mailFolder).ConfigureAwait(false);
            }
        }, cancellationToken);

    public async Task DisconnectAsync(string accountId)
    {
        if (_sessions.TryRemove(accountId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            await DisconnectAsync(id).ConfigureAwait(false);
        }
    }

    internal static string Describe(MailServer server) => $"{server.Host}:{server.Port}";

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string folder, FolderAccess access, CancellationToken cancellationToken)
    {
        var mailFolder = string.Equals(folder, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        await mailFolder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
        return mailFolder;
    }

    private static async Task CloseAsync(ImapClient client, IMailFolder folder)
    {
        if (folder.IsOpen && client.IsConnected)
        {
            await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static MailFolderInfo ToInfo(IMailFolder folder, FolderNamespace? ns)
    {
        var role = folder.Attributes switch
        {
            var a when a.HasFlag(FolderAttributes.Inbox) => FolderRole.Inbox,
            var a when a.HasFlag(FolderAttributes.Drafts) => FolderRole.Drafts,
            var a when a.HasFlag(FolderAttributes.Sent) => FolderRole.Sent,
            var a when a.HasFlag(FolderAttributes.Archive) => FolderRole.Archive,
            var a when a.HasFlag(FolderAttributes.Junk) => FolderRole.Junk,
            var a when a.HasFlag(FolderAttributes.Trash) => FolderRole.Trash,
            _ => string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase) ? FolderRole.Inbox : FolderRole.None,
        };

        var relative = ns is { Path.Length: > 0 } && folder.FullName.StartsWith(ns.Path, StringComparison.Ordinal)
            ? folder.FullName[ns.Path.Length..]
            : folder.FullName;
        var depth = folder.DirectorySeparator == '\0' ? 0 : relative.Trim(folder.DirectorySeparator).Count(c => c == folder.DirectorySeparator);
        var canSelect = !folder.Attributes.HasFlag(FolderAttributes.NoSelect);
        return new MailFolderInfo(folder.FullName, folder.Name, depth, role, canSelect, canSelect ? folder.Unread : 0, canSelect ? folder.Count : 0);
    }

    private static MessageHeader ToHeader(IMessageSummary summary)
    {
        var from = summary.Envelope?.From?.Mailboxes.FirstOrDefault();
        var date = summary.Envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.MinValue;
        var flags = summary.Flags ?? MessageFlags.None;
        return new MessageHeader(
            summary.UniqueId.Id,
            summary.Envelope?.Subject ?? string.Empty,
            from?.Name ?? string.Empty,
            from?.Address ?? string.Empty,
            date,
            flags.HasFlag(MessageFlags.Seen),
            flags.HasFlag(MessageFlags.Flagged),
            (long)(summary.Size ?? 0));
    }

    private ImapClient CreateImap() => new()
    {
        Timeout = (int)Timeout.TotalMilliseconds,
        ServerCertificateValidationCallback = _certificateValidation,
        CheckCertificateRevocation = true,
    };

    private async Task<T> WithImapAsync<T>(Account account, Func<ImapClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrAdd(account.Id, _ => new Session(CreateImap()));
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!session.Client.IsConnected || !session.Client.IsAuthenticated)
            {
                var password = _credentials.GetPassword(account.Id)
                    ?? throw new MailConnectionException(MailError.MissingPassword, Describe(account.Imap), "IMAP");
                if (session.Client.IsConnected)
                {
                    await session.Client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
                }

                await ConnectAsync(session.Client, account.Imap, "IMAP", account.UserName, password, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await action(session.Client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Classify(ex) is { } error)
            {
                // Connexion perdue en route : la prochaine opération se reconnecte.
                if (session.Client.IsConnected && error is MailError.Protocol or MailError.Timeout or MailError.ConnectionFailed)
                {
                    try
                    {
                        await session.Client.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Déjà coupée.
                    }
                }

                _logger.LogWarning("IMAP {Server} : {Error} ({Type})", Describe(account.Imap), error, ex.GetType().Name);
                throw new MailConnectionException(error, Describe(account.Imap), "IMAP", ex);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task ConnectAsync(MailService client, MailServer server, string protocol, string userName, string password, CancellationToken cancellationToken)
    {
        var options = server.Security == MailSecurity.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
        try
        {
            await client.ConnectAsync(server.Host, server.Port, options, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(userName, password, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Classify(ex) is { } error)
        {
            _logger.LogWarning("{Protocol} {Server} : {Error} ({Type})", protocol, Describe(server), error, ex.GetType().Name);
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Déjà coupée.
                }
            }

            throw new MailConnectionException(error, Describe(server), protocol, ex);
        }
    }

    /// <summary>Type d'erreur compréhensible ; <c>null</c> pour une annulation demandée (elle remonte telle quelle).</summary>
    internal static MailError? Classify(Exception ex) => ex switch
    {
        MailConnectionException mail => mail.Error,
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => MailError.HostNotFound,
        SocketException { SocketErrorCode: SocketError.TimedOut } => MailError.Timeout,
        SocketException => MailError.ConnectionFailed,
        SslHandshakeException { InnerException: System.Security.Authentication.AuthenticationException } => MailError.Certificate,
        SslHandshakeException => MailError.Tls,
        System.Security.Authentication.AuthenticationException => MailError.Certificate,
        MailKit.Security.AuthenticationException => MailError.Authentication,
        NotSupportedException when ex.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) => MailError.Tls,
        TimeoutException => MailError.Timeout,
        IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } } => MailError.Timeout,
        IOException => MailError.ConnectionFailed,
        ImapProtocolException or SmtpProtocolException or ImapCommandException or SmtpCommandException or ProtocolException or CommandException => MailError.Protocol,
        FolderNotFoundException => MailError.Protocol,
        _ => null,
    };

    private sealed class Session(ImapClient client) : IAsyncDisposable
    {
        public ImapClient Client { get; } = client;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public async ValueTask DisposeAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Client.IsConnected)
                {
                    await Client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Fermeture au mieux.
            }
            finally
            {
                Client.Dispose();
                Gate.Release();
            }
        }
    }
}
