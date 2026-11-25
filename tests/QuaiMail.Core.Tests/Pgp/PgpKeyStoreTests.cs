using System.Diagnostics;
using MimeKit;
using MimeKit.Cryptography;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Pgp;

/// <summary>Signature OpenPGP : clés créées pendant le test, vérification comme le ferait le destinataire.</summary>
public sealed class PgpKeyStoreTests : IDisposable
{
    private const string Passphrase = "phrase de passe d'été";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKeyRing Alice = TestPgpKeys.Create("Alice Martin <alice@example.org>", Passphrase);

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly PgpKeyStore _store;

    public PgpKeyStoreTests()
    {
        _paths = new AppPaths(_temp.Path);
        _store = new PgpKeyStore(_paths);
    }

    public void Dispose()
    {
        _store.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void An_exported_secret_key_is_imported_and_kept()
    {
        var changes = 0;
        _store.Changed += (_, _) => changes++;

        var key = Assert.Single(_store.Import(TestPgpKeys.Armored(Alice)));

        Assert.Equal(Convert.ToHexString(Alice.GetPublicKey().GetFingerprint()), key.Fingerprint);
        Assert.Equal("Alice Martin <alice@example.org>", key.UserId);
        Assert.Equal(["alice@example.org"], key.Addresses);
        Assert.Equal("RSA 2048", key.Algorithm);
        Assert.True(key.NeedsPassphrase);
        Assert.Null(key.Expires);
        Assert.Equal(1, changes);
        Assert.Matches("^([0-9A-F]{4} ){9}[0-9A-F]{4}$", key.DisplayFingerprint);

        // Gardée dans le dossier de Quai Mail, toujours chiffrée par sa phrase de passe (jamais écrite).
        using var reopened = new PgpKeyStore(_paths);
        Assert.Equal(key.Fingerprint, Assert.Single(reopened.Keys).Fingerprint);
        Assert.Equal(key.Fingerprint, reopened.FindForAddress("ALICE@example.org", Now)?.Fingerprint);
        Assert.Null(reopened.FindForAddress("bob@example.org", Now));
        foreach (var file in Directory.GetFiles(_paths.PgpDirectory))
        {
            Assert.DoesNotContain(Passphrase, File.ReadAllText(file), StringComparison.Ordinal);
        }

        // Importer de nouveau la même clé la remplace (pas de doublon).
        _store.Import(TestPgpKeys.Armored(Alice));
        Assert.Single(_store.Keys);
    }

    [Fact]
    public void A_public_key_or_an_unrelated_file_is_refused()
    {
        var publicOnly = Assert.Throws<PgpKeyException>(() => _store.Import(TestPgpKeys.ArmoredPublic(Alice)));
        Assert.Equal(PgpError.NoSecretKey, publicOnly.Error);

        var garbage = Assert.Throws<PgpKeyException>(() => _store.Import(new MemoryStream("Ceci n'est pas une clé."u8.ToArray())));
        Assert.Equal(PgpError.InvalidFile, garbage.Error);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public void A_signed_mail_is_pgp_mime_and_verifies_with_the_public_key_only()
    {
        var key = Assert.Single(_store.Import(TestPgpKeys.Armored(Alice)));
        var message = Message("Bonjour Bob,\n\nÀ demain, sous l'abri du quai.\n");

        _store.Sign(message, key, Passphrase, Now);

        var signed = Assert.IsType<MultipartSigned>(message.Body);
        Assert.Equal("application/pgp-signature", signed.ContentType.Parameters["protocol"]);
        Assert.Equal("pgp-sha256", signed.ContentType.Parameters["micalg"]);

        // Ce que reçoit le destinataire : le message relu tel qu'il a été écrit, vérifié avec la clé publique seule.
        var received = Transmit(message);
        using var verifierDirectory = new TempDirectory();
        using var verifier = new TestPgpKeys.Verifier(verifierDirectory.Path, Alice);
        var signature = Assert.Single(((MultipartSigned)received.Body).Verify(verifier));
        Assert.True(signature.Verify());
        Assert.Equal(Alice.GetPublicKey().KeyId, ((OpenPgpDigitalCertificate)signature.SignerCertificate).PublicKey.KeyId);
        Assert.Equal("Bonjour Bob,\n\nÀ demain, sous l'abri du quai.\n", received.TextBody.Replace("\r\n", "\n", StringComparison.Ordinal));

        // Un seul caractère changé en route : la signature ne tient plus.
        var tampered = Transmit(message).ToString().Replace("demain", "Demain", StringComparison.Ordinal);
        var altered = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(tampered)));
        var broken = Assert.Single(((MultipartSigned)altered.Body).Verify(verifier));
        Assert.False(broken.Verify());
    }

    [Fact]
    public void A_wrong_passphrase_signs_nothing_and_the_right_one_is_remembered_for_the_session()
    {
        var key = Assert.Single(_store.Import(TestPgpKeys.Armored(Alice)));
        var message = Message("Texte");
        var body = message.Body;

        var refused = Assert.Throws<PgpKeyException>(() => _store.Sign(message, key, "pas la bonne", Now));

        Assert.Equal(PgpError.WrongPassphrase, refused.Error);
        Assert.Same(body, message.Body);
        Assert.Null(_store.RememberedPassphrase(key.Fingerprint));

        _store.Sign(message, key, Passphrase, Now);
        Assert.Equal(Passphrase, _store.RememberedPassphrase(key.Fingerprint));
        _store.ForgetPassphrases();
        Assert.Null(_store.RememberedPassphrase(key.Fingerprint));
    }

    [Fact]
    public void An_expired_key_is_not_offered_and_does_not_sign()
    {
        var old = TestPgpKeys.Create("Alice <alice@example.org>", Passphrase, DateTime.UtcNow.AddDays(-10), TimeSpan.FromDays(5));
        var key = Assert.Single(_store.Import(TestPgpKeys.Armored(old)));

        Assert.True(key.IsExpired(Now));
        Assert.Null(_store.FindForAddress("alice@example.org", Now));
        var expired = Assert.Throws<PgpKeyException>(() => _store.Sign(Message("Texte"), key, Passphrase, Now));
        Assert.Equal(PgpError.Expired, expired.Error);
    }

    [Fact]
    public void A_removed_key_no_longer_signs()
    {
        var key = Assert.Single(_store.Import(TestPgpKeys.Armored(Alice)));
        _store.Sign(Message("Texte"), key, Passphrase, Now);

        Assert.True(_store.Remove(key.Fingerprint));

        Assert.Empty(_store.Keys);
        Assert.Null(_store.RememberedPassphrase(key.Fingerprint));
        Assert.Empty(new PgpKeyStore(_paths).Keys);
        Assert.Equal(PgpError.NoSigningKey, Assert.Throws<PgpKeyException>(() => _store.Sign(Message("Texte"), key, Passphrase, Now)).Error);
    }

    /// <summary>
    /// Avec le vrai GnuPG (s'il est installé) : clé Ed25519 créée par gpg avec une phrase de passe accentuée, exportée
    /// comme l'explique le guide, importée, puis la signature de Quai Mail vérifiée par <c>gpg --verify</c>.
    /// </summary>
    [Fact]
    public async Task A_key_made_by_gnupg_signs_mails_that_gnupg_verifies()
    {
        if (Gpg("--version").ExitCode != 0)
        {
            Assert.Skip("GnuPG absent : test d'interopérabilité non lancé.");
        }

        using var home = new TempDirectory();
        const string phrase = "clé d'été à Noël";
        string[] batch = ["--homedir", home.Path, "--batch", "--pinentry-mode", "loopback", "--passphrase", phrase];
        Assert.Equal(0, Gpg([.. batch, "--quick-gen-key", "Camille <camille@example.org>", "ed25519", "sign", "1y"]).ExitCode);
        var export = Gpg([.. batch, "--armor", "--export-secret-keys", "camille@example.org"]);
        Assert.Equal(0, export.ExitCode);

        var key = Assert.Single(_store.Import(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(export.Output))));
        Assert.Equal("Ed25519", key.Algorithm);
        Assert.NotNull(key.Expires);
        var message = Message("Signé par Quai Mail, vérifié par GnuPG.\n");
        _store.Sign(message, key, phrase, Now);

        var signed = (MultipartSigned)Transmit(message).Body;
        var content = Path.Combine(home.Path, "content.eml");
        var signature = Path.Combine(home.Path, "signature.asc");
        // Partie signée sous sa forme canonique (fins de ligne CRLF), comme la vérifie tout client mail.
        var canonical = FormatOptions.Default.Clone();
        canonical.NewLineFormat = NewLineFormat.Dos;
        await using (var file = File.Create(content))
        {
            await signed[0].WriteToAsync(canonical, file, TestContext.Current.CancellationToken);
        }

        await using (var file = File.Create(signature))
        {
            await ((MimePart)signed[1]).Content.DecodeToAsync(file, TestContext.Current.CancellationToken);
        }

        var verify = Gpg("--homedir", home.Path, "--batch", "--verify", signature, content);
        Assert.True(verify.ExitCode == 0, verify.Output);
        Assert.Contains("camille@example.org", verify.Output, StringComparison.Ordinal);
    }

    private static MimeMessage Message(string text)
    {
        var message = new MimeMessage { Subject = "Essai" };
        message.From.Add(new MailboxAddress("Alice Martin", "alice@example.org"));
        message.To.Add(new MailboxAddress("Bob", "bob@example.org"));
        message.Body = new TextPart("plain") { Text = text };
        return message;
    }

    private static MimeMessage Transmit(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    private static (int ExitCode, string Output) Gpg(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("gpg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit(60_000);
            return (process.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty);
        }
    }
}
