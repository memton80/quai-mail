using QuaiMail.Core.Accounts;
using QuaiMail.Core.Tests.Support;

// Faux serveur IMAP (TLS, IDLE) piloté par fichiers, pour le test de l'agent dans le CI :
//   dotnet run --project tests/QuaiMail.FakeMailServer -- <dossier>
// Écrit dans <dossier> : server.cer (certificat « localhost » à approuver), port.txt, puis en continu idling.txt
// (connexions en IDLE) et connections.txt. Un fichier deliver.txt fait arriver un nouveau mail ; stop.txt arrête tout.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage : QuaiMail.FakeMailServer <dossier>");
    return 2;
}

var directory = Directory.CreateDirectory(args[0]).FullName;
await using var server = new FakeImapServer(MailSecurity.SslOnConnect);
server.Folders.Add(new FakeFolder("INBOX"));
FakeImapServer.Fill(server.Folder("INBOX"), 3, DateTimeOffset.UtcNow);

File.WriteAllBytes(Path.Combine(directory, "server.cer"), TestCertificate.Server.RawData);
File.WriteAllText(Path.Combine(directory, "port.txt"), server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
Console.WriteLine($"Faux serveur IMAP sur localhost:{server.Port} ({server.UserName})");

var next = 4u;
var (idling, connections) = (-1, -1);
while (!File.Exists(Path.Combine(directory, "stop.txt")))
{
    var deliver = Path.Combine(directory, "deliver.txt");
    if (File.Exists(deliver))
    {
        File.Delete(deliver);
        await server.DeliverAsync("INBOX", new FakeMessage(next, $"Nouveau mail {next}", "Expéditrice de test", "test@example.org", DateTimeOffset.UtcNow));
        Console.WriteLine($"Mail {next} arrivé");
        next++;
    }

    if (server.Idling != idling || server.ConnectionCount != connections)
    {
        (idling, connections) = (server.Idling, server.ConnectionCount);
        File.WriteAllText(Path.Combine(directory, "idling.txt"), idling.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(directory, "connections.txt"), connections.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine($"IDLE : {idling}, connexions : {connections}");
    }

    await Task.Delay(100);
}

return 0;
