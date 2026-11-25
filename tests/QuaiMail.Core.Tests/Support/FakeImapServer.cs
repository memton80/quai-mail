using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QuaiMail.Core.Accounts;

namespace QuaiMail.Core.Tests.Support;

/// <summary>Un mail du faux serveur.</summary>
public sealed record FakeMessage(uint Uid, string Subject, string FromName, string FromAddress, DateTimeOffset Date, bool Seen = false, bool Flagged = false, int Size = 1200)
{
    public bool Deleted { get; init; }

    public bool Draft { get; init; }

    /// <summary>Mail complet (MIME) ; à défaut, un simple mail texte est fabriqué.</summary>
    public byte[]? Raw { get; init; }

    public byte[] Mime() => Raw ?? Encoding.UTF8.GetBytes(
        $"From: \"{FromName}\" <{FromAddress}>\r\nTo: alice@example.org\r\nSubject: {Subject}\r\nDate: {Date:R}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nCorps du message {Uid}.\r\n");
}

/// <summary>Un dossier du faux serveur.</summary>
public sealed class FakeFolder(string name, params string[] attributes)
{
    public string Name { get; } = name;

    public string[] Attributes { get; } = attributes;

    public uint UidValidity { get; set; } = 1;

    public List<FakeMessage> Messages { get; } = [];

    public bool CanSelect => !Attributes.Contains(@"\Noselect");
}

/// <summary>
/// Faux serveur IMAP4rev1 minimal, sur 127.0.0.1 : de quoi se connecter (TLS ou STARTTLS), s'identifier (AUTHENTICATE
/// PLAIN ou LOGIN), lister les dossiers avec leur état, ouvrir un dossier en lecture seule et lire des en-têtes
/// (ENVELOPE, FLAGS, UID, INTERNALDATE, RFC822.SIZE). Toute commande d'écriture est refusée et notée.
/// </summary>
public sealed partial class FakeImapServer : FakeServer
{
    private const char Separator = '/';

    public FakeImapServer(MailSecurity security = MailSecurity.SslOnConnect)
        : base(security)
    {
    }

    public string UserName { get; set; } = "alice@example.org";

    public string Password { get; set; } = "mot de passe de test";

    public List<FakeFolder> Folders { get; } = [];

    public FakeFolder Folder(string name) => Folders.Single(f => f.Name == name);

    /// <summary>Remplit un dossier de <paramref name="count"/> mails (UID 1 à count, du plus ancien au plus récent).</summary>
    public static void Fill(FakeFolder folder, int count, DateTimeOffset newest)
    {
        for (var i = 1; i <= count; i++)
        {
            folder.Messages.Add(new FakeMessage((uint)i, $"Message {i}", $"Expéditeur {i}", $"expediteur{i}@example.org", newest.AddHours(i - count), Seen: i % 3 == 0));
        }
    }

    protected override async Task ServeAsync(Connection connection, CancellationToken cancellationToken)
    {
        var authenticated = false;
        FakeFolder? selected = null;
        await connection.WriteAsync($"* OK [CAPABILITY {Capabilities(connection, authenticated)}] Faux serveur IMAP prêt\r\n", cancellationToken);

        while (await ReadCommandAsync(connection, cancellationToken) is { } line)
        {
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                await connection.WriteAsync("* BAD Commande vide\r\n", cancellationToken);
                continue;
            }

            var tag = line[..space];
            var rest = line[(space + 1)..];
            var args = Tokenize(rest);
            var command = args.Count > 0 ? args[0].ToUpperInvariant() : string.Empty;
            if (command == "UID" && args.Count > 1)
            {
                command = "UID " + args[1].ToUpperInvariant();
                args.RemoveAt(0);
            }

            Record(command is "LOGIN" or "AUTHENTICATE" ? command : rest);
            switch (command)
            {
                case "CAPABILITY":
                    await connection.WriteAsync($"* CAPABILITY {Capabilities(connection, authenticated)}\r\n{tag} OK CAPABILITY terminé\r\n", cancellationToken);
                    break;

                case "STARTTLS" when !connection.IsTls:
                    await connection.WriteAsync($"{tag} OK Début de TLS\r\n", cancellationToken);
                    await connection.StartTlsAsync(cancellationToken);
                    break;

                case "NOOP":
                case "CHECK":
                    await connection.WriteAsync($"{tag} OK NOOP terminé\r\n", cancellationToken);
                    break;

                case "LOGOUT":
                    await connection.WriteAsync($"* BYE Au revoir\r\n{tag} OK LOGOUT terminé\r\n", cancellationToken);
                    return;

                case "LOGIN" when connection.IsTls:
                    authenticated = args.Count >= 3 && args[1] == UserName && args[2] == Password;
                    await connection.WriteAsync(authenticated ? $"{tag} OK [CAPABILITY {Capabilities(connection, true)}] Connecté\r\n" : $"{tag} NO [AUTHENTICATIONFAILED] Identifiants refusés\r\n", cancellationToken);
                    break;

                case "AUTHENTICATE" when connection.IsTls && args.Count >= 2 && args[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase):
                    var response = args.Count >= 3 ? args[2] : null;
                    if (response is null)
                    {
                        await connection.WriteAsync("+ \r\n", cancellationToken);
                        response = await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
                    }

                    authenticated = CheckPlain(response);
                    await connection.WriteAsync(authenticated ? $"{tag} OK [CAPABILITY {Capabilities(connection, true)}] Connecté\r\n" : $"{tag} NO [AUTHENTICATIONFAILED] Identifiants refusés\r\n", cancellationToken);
                    break;

                case "LIST" or "LSUB" when authenticated && args.Count >= 3:
                    await connection.WriteAsync(List(command, args[1], args[2]) + $"{tag} OK {command} terminé\r\n", cancellationToken);
                    break;

                case "STATUS" when authenticated && args.Count >= 2 && Find(args[1]) is { } statusFolder:
                    var unseen = statusFolder.Messages.Count(m => !m.Seen);
                    await connection.WriteAsync($"* STATUS {Quote(statusFolder.Name)} (MESSAGES {statusFolder.Messages.Count} UNSEEN {unseen})\r\n{tag} OK STATUS terminé\r\n", cancellationToken);
                    break;

                case "EXAMINE" or "SELECT" when authenticated && args.Count >= 2 && Find(args[1]) is { CanSelect: true } folder:
                    selected = folder;
                    var next = folder.Messages.Count == 0 ? 1 : folder.Messages.Max(m => m.Uid) + 1;
                    await connection.WriteAsync(
                        "* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)\r\n"
                        + $"* {folder.Messages.Count} EXISTS\r\n* 0 RECENT\r\n"
                        + $"* OK [UIDVALIDITY {folder.UidValidity}] UID valides\r\n* OK [UIDNEXT {next}] UID suivant\r\n"
                        + $"{tag} OK [{(command == "SELECT" ? "READ-WRITE" : "READ-ONLY")}] {command} terminé\r\n",
                        cancellationToken);
                    break;

                case "FETCH" when selected is not null && args.Count >= 3:
                    await connection.WriteAsync(Fetch(selected, args[1], byUid: false) + $"{tag} OK FETCH terminé\r\n", cancellationToken);
                    break;

                case "UID FETCH" when selected is not null && args.Count >= 3 && args[2].Contains("BODY", StringComparison.OrdinalIgnoreCase):
                    await WriteBodiesAsync(connection, selected, args[1], cancellationToken);
                    await connection.WriteAsync($"{tag} OK UID FETCH terminé\r\n", cancellationToken);
                    break;

                case "UID STORE" when selected is not null && args.Count >= 4:
                    Store(selected, args[1], args[2], args[3]);
                    await connection.WriteAsync($"{tag} OK UID STORE terminé\r\n", cancellationToken);
                    break;

                case "UID FETCH" when selected is not null && args.Count >= 3:
                    await connection.WriteAsync(Fetch(selected, args[1], byUid: true) + $"{tag} OK UID FETCH terminé\r\n", cancellationToken);
                    break;

                case "APPEND" when authenticated && args.Count >= 3 && Find(args[1]) is { } target:
                    var appended = Append(target, args);
                    await connection.WriteAsync($"{tag} OK [APPENDUID {target.UidValidity} {appended}] APPEND terminé\r\n", cancellationToken);
                    break;

                case "UID EXPUNGE" when selected is not null && args.Count >= 2:
                    var max = selected.Messages.Count == 0 ? 0 : selected.Messages.Max(m => m.Uid);
                    for (var i = selected.Messages.Count - 1; i >= 0; i--)
                    {
                        if (selected.Messages[i].Deleted && InSet(args[1], selected.Messages[i].Uid, max))
                        {
                            await connection.WriteAsync($"* {i + 1} EXPUNGE\r\n", cancellationToken);
                            selected.Messages.RemoveAt(i);
                        }
                    }

                    await connection.WriteAsync($"{tag} OK UID EXPUNGE terminé\r\n", cancellationToken);
                    break;

                case "EXPUNGE" when selected is not null:
                    // Effacement général : noté (les tests vérifient qu'il n'est jamais demandé), puis refusé.
                    await connection.WriteAsync($"{tag} NO EXPUNGE général refusé par le faux serveur\r\n", cancellationToken);
                    break;

                case "CLOSE" or "UNSELECT" when selected is not null:
                    selected = null;
                    await connection.WriteAsync($"{tag} OK {command} terminé\r\n", cancellationToken);
                    break;

                default:
                    await connection.WriteAsync($"{tag} BAD Commande non prise en charge par le faux serveur\r\n", cancellationToken);
                    break;
            }
        }
    }

    private static string Capabilities(Connection connection, bool authenticated) =>
        !connection.IsTls ? "IMAP4rev1 STARTTLS LOGINDISABLED"
        : authenticated ? "IMAP4rev1 UNSELECT UIDPLUS"
        : "IMAP4rev1 AUTH=PLAIN SASL-IR";

    private bool CheckPlain(string base64)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim())).Split('\0');
            return parts.Length == 3 && parts[1] == UserName && parts[2] == Password;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private FakeFolder? Find(string name) =>
        Folders.FirstOrDefault(f => string.Equals(f.Name, name, f.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private string List(string command, string reference, string pattern)
    {
        var builder = new StringBuilder();
        var full = reference + pattern;
        if (full.Length == 0)
        {
            return $"* {command} (\\Noselect) \"{Separator}\" \"\"\r\n";
        }

        var regex = new Regex("^" + Regex.Escape(full).Replace(@"\*", ".*", StringComparison.Ordinal).Replace("%", $"[^{Separator}]*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant);
        foreach (var folder in Folders)
        {
            if (regex.IsMatch(folder.Name) || (folder.Name == "INBOX" && regex.IsMatch("INBOX".ToUpperInvariant())) || string.Equals(full, folder.Name, StringComparison.OrdinalIgnoreCase) && folder.Name == "INBOX")
            {
                var children = Folders.Any(f => f.Name.StartsWith(folder.Name + Separator, StringComparison.Ordinal)) ? "\\HasChildren" : "\\HasNoChildren";
                var attributes = string.Join(' ', folder.Attributes.Append(children));
                builder.Append(CultureInfo.InvariantCulture, $"* {command} ({attributes}) \"{Separator}\" {Quote(folder.Name)}\r\n");
            }
        }

        return builder.ToString();
    }

    private static string Fetch(FakeFolder folder, string set, bool byUid)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < folder.Messages.Count; index++)
        {
            var message = folder.Messages[index];
            var number = index + 1;
            if (!InSet(set, byUid ? message.Uid : (uint)number, byUid ? folder.Messages.Max(m => m.Uid) : (uint)folder.Messages.Count))
            {
                continue;
            }

            var flags = string.Join(' ', new[] { message.Seen ? "\\Seen" : null, message.Flagged ? "\\Flagged" : null }.OfType<string>());
            var date = message.Date.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + Offset(message.Date.Offset);
            var internalDate = message.Date.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + Offset(message.Date.Offset);
            var at = message.FromAddress.IndexOf('@');
            var from = $"(({Quote(message.FromName)} NIL {Quote(message.FromAddress[..at])} {Quote(message.FromAddress[(at + 1)..])}))";
            var to = "((\"Alice\" NIL \"alice\" \"example.org\"))";
            builder.Append(CultureInfo.InvariantCulture,
                $"* {number} FETCH (UID {message.Uid} FLAGS ({flags}) INTERNALDATE \"{internalDate}\" RFC822.SIZE {message.Size} "
                + $"ENVELOPE (\"{date}\" {Quote(EncodeWord(message.Subject))} {from} {from} {from} {to} NIL NIL NIL \"<{message.Uid}@example.org>\"))\r\n");
        }

        return builder.ToString();
    }

    /// <summary>APPEND dossier [(drapeaux)] [date] message : le mail est ajouté avec un nouvel UID.</summary>
    private static uint Append(FakeFolder folder, List<string> args)
    {
        var raw = args[^1];
        var flags = args.Skip(2).Take(args.Count - 3).FirstOrDefault(a => a.StartsWith('(')) ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(raw);
        var mime = MimeKit.MimeMessage.Load(new MemoryStream(bytes));
        var from = mime.From.Mailboxes.FirstOrDefault();
        var uid = folder.Messages.Count == 0 ? 1 : folder.Messages.Max(m => m.Uid) + 1;
        folder.Messages.Add(new FakeMessage(uid, mime.Subject ?? string.Empty, from?.Name ?? string.Empty, from?.Address ?? "inconnu@example.org", mime.Date,
            Seen: flags.Contains("\\Seen", StringComparison.OrdinalIgnoreCase), Size: bytes.Length)
        {
            Raw = bytes,
            Draft = flags.Contains("\\Draft", StringComparison.OrdinalIgnoreCase),
        });
        return uid;
    }

    private static async Task WriteBodiesAsync(Connection connection, FakeFolder folder, string set, CancellationToken cancellationToken)
    {
        var max = folder.Messages.Count == 0 ? 0 : folder.Messages.Max(m => m.Uid);
        for (var index = 0; index < folder.Messages.Count; index++)
        {
            var message = folder.Messages[index];
            if (InSet(set, message.Uid, max))
            {
                var mime = message.Mime();
                await connection.WriteAsync($"* {index + 1} FETCH (UID {message.Uid} BODY[] {{{mime.Length}}}\r\n", cancellationToken);
                await connection.WriteAsync(mime, cancellationToken);
                await connection.WriteAsync(")\r\n", cancellationToken);
            }
        }
    }

    /// <summary>Change les drapeaux \\Seen et \\Flagged (seuls gérés) ; tout le reste est ignoré.</summary>
    private static void Store(FakeFolder folder, string set, string action, string flags)
    {
        var max = folder.Messages.Count == 0 ? 0 : folder.Messages.Max(m => m.Uid);
        var add = action.StartsWith('+');
        var remove = action.StartsWith('-');
        for (var index = 0; index < folder.Messages.Count; index++)
        {
            var message = folder.Messages[index];
            if (!InSet(set, message.Uid, max))
            {
                continue;
            }

            var seen = flags.Contains("\\Seen", StringComparison.OrdinalIgnoreCase);
            var flagged = flags.Contains("\\Flagged", StringComparison.OrdinalIgnoreCase);
            var deleted = flags.Contains("\\Deleted", StringComparison.OrdinalIgnoreCase);
            folder.Messages[index] = message with
            {
                Seen = seen ? add || (!remove && message.Seen) : message.Seen,
                Flagged = flagged ? add || (!remove && message.Flagged) : message.Flagged,
                Deleted = deleted ? add || (!remove && message.Deleted) : message.Deleted,
            };
        }
    }

    private static bool InSet(string set, uint value, uint max)
    {
        foreach (var part in set.Split(','))
        {
            var bounds = part.Split(':');
            uint Parse(string s) => s == "*" ? max : uint.Parse(s, CultureInfo.InvariantCulture);
            var low = Parse(bounds[0]);
            var high = bounds.Length > 1 ? Parse(bounds[1]) : low;
            if (value >= Math.Min(low, high) && value <= Math.Max(low, high))
            {
                return true;
            }
        }

        return false;
    }

    private static string Offset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);

    /// <summary>Objet non ASCII : mot encodé (RFC 2047), comme sur un vrai serveur.</summary>
    private static string EncodeWord(string text) =>
        text.All(char.IsAscii) ? text : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Lit une commande, littéraux synchronisés compris (« {12} » : le serveur invite à envoyer la suite).</summary>
    private static async Task<string?> ReadCommandAsync(Connection connection, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await connection.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            var literal = LiteralSuffix().Match(line);
            if (!literal.Success)
            {
                return builder.Append(line).ToString();
            }

            builder.Append(line.AsSpan(0, literal.Index));
            var length = int.Parse(literal.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!literal.Groups[2].Success)
            {
                await connection.WriteAsync("+ Prêt\r\n", cancellationToken);
            }

            var bytes = await connection.ReadBytesAsync(length, cancellationToken);
            builder.Append(Quote(Encoding.UTF8.GetString(bytes)));
        }
    }

    /// <summary>Atomes, chaînes entre guillemets, listes entre parenthèses (gardées telles quelles).</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                var value = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    value.Append(text[i++]);
                }

                i++;
                tokens.Add(value.ToString());
            }
            else if (text[i] == '(')
            {
                var depth = 0;
                var start = i;
                do
                {
                    depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
                    i++;
                }
                while (i < text.Length && depth > 0);
                tokens.Add(text[start..i]);
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != ' ')
                {
                    i++;
                }

                tokens.Add(text[start..i]);
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"\{(\d+)(\+)?\}$")]
    private static partial Regex LiteralSuffix();
}
