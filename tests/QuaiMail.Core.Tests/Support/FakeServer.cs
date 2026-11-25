using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using QuaiMail.Core.Accounts;

namespace QuaiMail.Core.Tests.Support;

/// <summary>
/// Base des faux serveurs locaux (IMAP, SMTP) : écoute sur 127.0.0.1, port libre, TLS dès la connexion ou par
/// STARTTLS avec <see cref="TestCertificate"/>. Jamais de vrai serveur ni de vrai compte dans les tests.
/// </summary>
public abstract class FakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accepting;

    protected FakeServer(MailSecurity security)
    {
        Security = security;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public MailSecurity Security { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public MailServer Endpoint => new() { Host = "localhost", Port = Port, Security = Security };

    /// <summary>Commandes reçues (sans les secrets : la ligne d'identification est masquée).</summary>
    public List<string> Commands { get; } = [];

    public int ConnectionCount { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception)
        {
            // Arrêt.
        }

        Task[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }

        await Task.WhenAll(connections.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    protected abstract Task ServeAsync(Connection connection, CancellationToken cancellationToken);

    protected void Record(string command)
    {
        lock (Commands)
        {
            Commands.Add(command);
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            ConnectionCount++;
            var task = Task.Run(async () =>
            {
                using (client)
                {
                    var connection = new Connection(client.GetStream());
                    try
                    {
                        if (Security == MailSecurity.SslOnConnect)
                        {
                            await connection.StartTlsAsync(_stop.Token);
                        }

                        await ServeAsync(connection, _stop.Token);
                    }
                    catch (Exception) when (_stop.IsCancellationRequested)
                    {
                    }
                    catch (IOException)
                    {
                        // Client parti.
                    }
                    catch (System.Security.Authentication.AuthenticationException)
                    {
                        // Le client a refusé le certificat de test (validation stricte) : c'est ce qui est attendu.
                    }
                    finally
                    {
                        await connection.DisposeAsync();
                    }
                }
            });
            lock (_connections)
            {
                _connections.Add(task);
            }
        }
    }

    /// <summary>Une connexion : lecture ligne à ligne (octets), écriture, passage en TLS.</summary>
    public sealed class Connection(Stream stream) : IAsyncDisposable
    {
        private Stream _stream = stream;
        private readonly MemoryStream _pending = new();

        public bool IsTls { get; private set; }

        public async Task StartTlsAsync(CancellationToken cancellationToken)
        {
            var tls = new SslStream(_stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = TestCertificate.Server }, cancellationToken);
            _stream = tls;
            IsTls = true;
        }

        public Task WriteAsync(string text, CancellationToken cancellationToken) => WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);

        public async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        /// <summary>Une ligne sans son CRLF, ou <c>null</c> si le client a fermé.</summary>
        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new MemoryStream();
            var buffer = new byte[1];
            while (true)
            {
                var read = await _stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return line.Length == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
                }

                if (buffer[0] == '\n')
                {
                    var bytes = line.ToArray();
                    var length = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
                    return Encoding.UTF8.GetString(bytes, 0, length);
                }

                line.WriteByte(buffer[0]);
            }
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            var bytes = new byte[count];
            await _stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            await _pending.DisposeAsync();
        }
    }
}
