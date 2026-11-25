using System.Text;
using QuaiMail.Core.Accounts;

namespace QuaiMail.Core.Tests.Support;

/// <summary>
/// Faux serveur SMTP minimal : TLS (ou STARTTLS), EHLO, AUTH PLAIN / LOGIN, QUIT. Il n'accepte aucun envoi : toute
/// commande d'envoi (MAIL, RCPT, DATA…) est refusée et notée, pour prouver qu'aucun mail n'est jamais envoyé.
/// </summary>
public sealed class FakeSmtpServer(MailSecurity security = MailSecurity.SslOnConnect) : FakeServer(security)
{
    public string UserName { get; set; } = "alice@example.org";

    public string Password { get; set; } = "mot de passe de test";

    public int SendAttempts { get; private set; }

    protected override async Task ServeAsync(Connection connection, CancellationToken cancellationToken)
    {
        await connection.WriteAsync("220 localhost Faux serveur SMTP\r\n", cancellationToken);
        while (await connection.ReadLineAsync(cancellationToken) is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            Record(verb == "AUTH" ? "AUTH" : line);
            switch (verb)
            {
                case "EHLO" or "HELO":
                    var extensions = connection.IsTls ? "250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n" : "250-STARTTLS\r\n250 8BITMIME\r\n";
                    await connection.WriteAsync("250-localhost\r\n" + extensions, cancellationToken);
                    break;

                case "STARTTLS" when !connection.IsTls:
                    await connection.WriteAsync("220 2.0.0 Début de TLS\r\n", cancellationToken);
                    await connection.StartTlsAsync(cancellationToken);
                    break;

                case "AUTH" when connection.IsTls:
                    var parts = line.Split(' ');
                    var ok = false;
                    if (parts.Length >= 2 && parts[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        var response = parts.Length >= 3 ? parts[2] : await Ask(connection, string.Empty, cancellationToken);
                        var fields = Decode(response).Split('\0');
                        ok = fields.Length == 3 && fields[1] == UserName && fields[2] == Password;
                    }
                    else if (parts.Length >= 2 && parts[1].Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        var user = parts.Length >= 3 ? Decode(parts[2]) : Decode(await Ask(connection, "VXNlcm5hbWU6", cancellationToken));
                        var password = Decode(await Ask(connection, "UGFzc3dvcmQ6", cancellationToken));
                        ok = user == UserName && password == Password;
                    }

                    await connection.WriteAsync(ok ? "235 2.7.0 Identifié\r\n" : "535 5.7.8 Identifiants refusés\r\n", cancellationToken);
                    break;

                case "NOOP" or "RSET":
                    await connection.WriteAsync("250 2.0.0 OK\r\n", cancellationToken);
                    break;

                case "QUIT":
                    await connection.WriteAsync("221 2.0.0 Au revoir\r\n", cancellationToken);
                    return;

                case "MAIL" or "RCPT" or "DATA" or "BDAT":
                    SendAttempts++;
                    await connection.WriteAsync("554 5.7.1 Le faux serveur n'accepte aucun envoi\r\n", cancellationToken);
                    break;

                default:
                    await connection.WriteAsync("502 5.5.2 Commande non prise en charge\r\n", cancellationToken);
                    break;
            }
        }
    }

    private static async Task<string> Ask(Connection connection, string challenge, CancellationToken cancellationToken)
    {
        await connection.WriteAsync($"334 {challenge}\r\n", cancellationToken);
        return await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
    }

    private static string Decode(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
