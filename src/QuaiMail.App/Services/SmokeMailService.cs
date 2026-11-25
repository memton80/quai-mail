using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;

namespace QuaiMail.App.Services;

/// <summary>
/// Serveur de mail du test de fumée : des données de test en mémoire, aucune connexion réseau, jamais de vrai compte.
/// Il accepte un seul mot de passe (<see cref="Password"/>), comme un vrai serveur refuserait les autres.
/// </summary>
internal sealed class SmokeMailService : IMailService
{
    public const string Password = "mot de passe du test de fumée";

    private static readonly DateTimeOffset Newest = new(2026, 11, 25, 9, 30, 0, TimeSpan.FromHours(1));

    private static readonly MailFolderInfo[] Folders =
    [
        new("INBOX", "INBOX", 0, FolderRole.Inbox, true, 12, 120),
        new("Brouillons", "Brouillons", 0, FolderRole.Drafts, true, 0, 0),
        new("Envoyés", "Envoyés", 0, FolderRole.Sent, true, 0, 3),
        new("Corbeille", "Corbeille", 0, FolderRole.Trash, true, 0, 0),
        new("Archives", "Archives", 0, FolderRole.None, false, 0, 0),
        new("Archives/2026", "2026", 1, FolderRole.None, true, 0, 1),
    ];

    private static readonly string[] Senders = ["Camille Martin", "Service client", "Léa Dubois", "Mairie de Lyon", "Hugo Bernard"];

    private static readonly string[] Subjects =
    [
        "Réunion de jeudi", "Votre facture de novembre", "Photos du week-end", "Inscription à la cantine", "Re: devis cuisine",
    ];

    public Task VerifyAsync(Account account, string password, CancellationToken cancellationToken = default) =>
        password == Password
            ? Task.CompletedTask
            : Task.FromException(new MailConnectionException(MailError.Authentication, $"{account.Imap.Host}:{account.Imap.Port}", "IMAP"));

    public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(Account account, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailFolderInfo>>(Folders);

    public async Task<MessagePage> GetMessagesAsync(Account account, string folder, int skip, int take, CancellationToken cancellationToken = default)
    {
        // Un peu de délai, comme un vrai serveur : la barre de chargement apparaît.
        await Task.Delay(150, cancellationToken);
        var total = Folders.FirstOrDefault(f => f.FullName == folder)?.Total ?? 0;
        var messages = Enumerable.Range(0, Math.Max(0, Math.Min(take, total - skip)))
            .Select(i => total - skip - i)
            .Select(uid => new MessageHeader(
                (uint)uid,
                folder == "Archives/2026" ? "Facture d'électricité" : Subjects[uid % Subjects.Length],
                Senders[uid % Senders.Length],
                $"contact{uid % Senders.Length}@example.org",
                Newest.AddHours((uid - total) * 7),
                uid <= total - 12,
                uid % 17 == 0,
                2048))
            .ToList();
        return new MessagePage(1, total, messages);
    }

    public Task DisconnectAsync(string accountId) => Task.CompletedTask;
}

/// <summary>Pas de réseau dans les tests de fumée : l'autoconfiguration n'interroge rien.</summary>
internal sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("Pas de réseau pendant le test de fumée."));
}
