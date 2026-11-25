using System.Net;
using MimeKit;
using Org.BouncyCastle.Bcpg.OpenPgp;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Pgp;

/// <summary>
/// Mails signés reçus : la signature n'est pas une pièce jointe, et elle est vérifiée avec la clé trouvée parmi les
/// vôtres, dans Kleopatra, ou sur le serveur de clés (avec accord) ; clés créées pendant le test.
/// </summary>
public sealed class SignatureCheckerTests : IDisposable
{
    private const string Passphrase = "phrase de passe du test";
    private static readonly PgpSecretKeyRing Lea = TestPgpKeys.Create("Léa Dubois <lea@example.org>", Passphrase);
    private static readonly PgpSecretKeyRing Mallory = TestPgpKeys.Create("Mallory <mallory@example.org>", Passphrase);

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly PgpKeyStore _own;
    private readonly FakeKeyServer _server = new();
    private readonly MovingTime _time = new(DateTimeOffset.UtcNow);

    public SignatureCheckerTests()
    {
        _paths = new AppPaths(_temp.Path);
        _own = new PgpKeyStore(_paths);
    }

    public void Dispose()
    {
        _own.Dispose();
        _temp.Dispose();
    }

    private static string Fingerprint(PgpSecretKeyRing ring) => Convert.ToHexString(ring.GetPublicKey().GetFingerprint());

    private static MessageContent Received(PgpSecretKeyRing signer, string from = "lea@example.org", Func<string, string>? tamper = null, bool attachment = false)
    {
        var builder = new BodyBuilder { TextBody = "Bonjour,\nle devis est en pièce jointe.\n" };
        if (attachment)
        {
            builder.Attachments.Add("devis.pdf", "%PDF-1.7 devis"u8.ToArray(), new ContentType("application", "pdf"));
        }

        var message = new MimeMessage([new MailboxAddress("Léa", from)], [new MailboxAddress("Camille", "camille@example.org")], "Devis", builder.ToMessageBody());
        return MessageContent.FromMime(7, TestPgpKeys.Signed(message, signer, Passphrase, tamper));
    }

    private SignatureChecker Checker(IGnuPgLocator? gnupg = null) => new(_paths, _own, gnupg, _server, _time);

    [Fact]
    public void The_signature_is_not_an_attachment_but_real_attachments_stay()
    {
        var content = Received(Lea, attachment: true);

        Assert.True(content.IsPgpSigned);
        Assert.Equal(["devis.pdf"], content.Attachments.Select(a => a.FileName));
        Assert.Contains("le devis est en pièce jointe", content.Text, StringComparison.Ordinal);
        Assert.False(MessageContent.FromMime(8, new MimeMessage { Body = new TextPart("plain") { Text = "Pas signé" } }).IsPgpSigned);
    }

    [Fact]
    public async Task An_unknown_key_is_reported_and_nobody_is_asked_without_permission()
    {
        _server.Publish(Lea);

        var report = await Checker().CheckAsync(Received(Lea), useKeyServer: false, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureState.UnknownKey, report.State);
        Assert.False(report.LookedUp);
        Assert.Empty(_server.Requests);
        string[] issuers = [Fingerprint(Lea), Lea.GetPublicKey().KeyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)];
        Assert.Contains(report.Issuer, issuers);
    }

    [Fact]
    public async Task A_key_published_on_the_server_verifies_the_mail_and_is_kept_for_a_week()
    {
        _server.Publish(Lea);
        var checker = Checker();
        var ct = TestContext.Current.CancellationToken;

        var report = await checker.CheckAsync(Received(Lea), useKeyServer: true, ct);

        Assert.Equal(SignatureState.Valid, report.State);
        Assert.Equal(SignatureKeySource.KeyServer, report.Source);
        Assert.Equal(Fingerprint(Lea), report.Fingerprint);
        Assert.Equal("Léa Dubois <lea@example.org>", report.SignerUserId);
        Assert.Single(_server.Requests);
        Assert.True(File.Exists(Path.Combine(_paths.PgpKeyServerCache, Fingerprint(Lea) + ".asc")));

        // Gardée : vérifiée sans redemander, même sans accord pour le serveur ; redemandée au bout d'une semaine.
        Assert.Equal(SignatureState.Valid, (await checker.CheckAsync(Received(Lea), useKeyServer: false, ct)).State);
        Assert.Equal(SignatureState.Valid, (await checker.CheckAsync(Received(Lea), useKeyServer: true, ct)).State);
        Assert.Single(_server.Requests);
        _time.Advance(TimeSpan.FromDays(8));
        Assert.Equal(SignatureState.Valid, (await checker.CheckAsync(Received(Lea), useKeyServer: true, ct)).State);
        Assert.Equal(2, _server.Requests.Count);

        // Hors connexion après une semaine : la clé gardée sert encore.
        _time.Advance(TimeSpan.FromDays(8));
        _server.Unreachable = true;
        var offline = await checker.CheckAsync(Received(Lea), useKeyServer: true, ct);
        Assert.Equal(SignatureState.Valid, offline.State);
        Assert.False(offline.ServerUnreachable);
    }

    [Fact]
    public async Task A_mail_modified_after_its_signature_is_invalid()
    {
        _server.Publish(Lea);

        var report = await Checker().CheckAsync(
            Received(Lea, tamper: raw => raw.Contains("Bonjour,", StringComparison.Ordinal) ? raw.Replace("Bonjour,", "Payez ici", StringComparison.Ordinal) : throw new InvalidOperationException("Texte à altérer introuvable")),
            useKeyServer: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(SignatureState.Invalid, report.State);
    }

    [Fact]
    public async Task A_valid_signature_by_a_key_of_another_address_does_not_prove_the_sender()
    {
        _server.Publish(Mallory);

        var report = await Checker().CheckAsync(Received(Mallory, from: "lea@example.org"), useKeyServer: true, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureState.OtherIdentity, report.State);
        Assert.Equal("Mallory <mallory@example.org>", report.SignerUserId);
    }

    [Fact]
    public async Task A_key_published_without_a_confirmed_address_proves_nothing_about_the_sender()
    {
        // keys.openpgp.org ne publie les adresses que confirmées par leur propriétaire : ici, aucune.
        var key = PgpPublicKey.RemoveCertification(Lea.GetPublicKey(), "Léa Dubois <lea@example.org>");
        using var output = new MemoryStream();
        using (var armor = new Org.BouncyCastle.Bcpg.ArmoredOutputStream(output))
        {
            key.Encode(armor);
        }

        _server.Publish(Lea, System.Text.Encoding.ASCII.GetString(output.ToArray()));

        var report = await Checker().CheckAsync(Received(Lea), useKeyServer: true, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureState.OtherIdentity, report.State);
        Assert.Null(report.SignerUserId);
    }

    [Fact]
    public async Task Your_own_keys_then_kleopatra_are_tried_before_the_server()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var armored = TestPgpKeys.Armored(Lea))
        {
            _own.Import(armored);
        }

        var own = await Checker().CheckAsync(Received(Lea), useKeyServer: true, ct);
        Assert.Equal((SignatureState.Valid, SignatureKeySource.OwnKey), (own.State, own.Source));

        var kleopatra = new KleopatraWith(Mallory);
        var fromKleopatra = await Checker(new Locator(kleopatra)).CheckAsync(Received(Mallory, from: "mallory@example.org"), useKeyServer: true, ct);
        Assert.Equal((SignatureState.Valid, SignatureKeySource.Kleopatra), (fromKleopatra.State, fromKleopatra.Source));
        Assert.Single(kleopatra.Asked);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_silent_server_leaves_the_key_unknown_and_says_so()
    {
        _server.Unreachable = true;

        var report = await Checker().CheckAsync(Received(Lea), useKeyServer: true, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureState.UnknownKey, report.State);
        Assert.True(report.ServerUnreachable);
        Assert.True(report.LookedUp);
    }

    [Fact]
    public async Task A_key_already_expired_when_signing_is_not_trusted()
    {
        var expired = TestPgpKeys.Create("Vieille clé <lea@example.org>", Passphrase, DateTime.UtcNow.AddDays(-3), TimeSpan.FromDays(1));
        _server.Publish(expired);

        var report = await Checker().CheckAsync(Received(expired), useKeyServer: true, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureState.KeyNotValid, report.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Keys_openpgp_org_is_asked_by_fingerprint_only(HttpStatusCode status, bool found)
    {
        var handler = new RecordingHandler(status, status == HttpStatusCode.OK ? TestPgpKeys.PublicText(Lea) : "Not found");
        using var server = new OpenPgpKeyServer(handler);

        var key = await server.FindAsync(Fingerprint(Lea).ToLowerInvariant(), TestContext.Current.CancellationToken);

        Assert.Equal(found, key is not null);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://keys.openpgp.org/vks/v1/by-fingerprint/{Fingerprint(Lea)}", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith("QuaiMail/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Equal("https://keys.openpgp.org/vks/v1/by-keyid/0123456789ABCDEF", OpenPgpKeyServer.Lookup("0123456789abcdef").ToString());
        Assert.Throws<ArgumentException>(() => OpenPgpKeyServer.Lookup("lea@example.org"));
    }

    [Fact]
    public async Task A_server_error_or_a_strange_answer_is_an_error_not_a_key()
    {
        using var failing = new OpenPgpKeyServer(new RecordingHandler(HttpStatusCode.InternalServerError, "oups"));
        using var strange = new OpenPgpKeyServer(new RecordingHandler(HttpStatusCode.OK, "<html>portail captif</html>"));

        await Assert.ThrowsAsync<HttpRequestException>(() => failing.FindAsync(Fingerprint(Lea), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => strange.FindAsync(Fingerprint(Lea), TestContext.Current.CancellationToken));
    }

    private sealed class MovingTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Locator(IGnuPg gnupg) : IGnuPgLocator
    {
        public IGnuPg? Find() => gnupg;
    }

    /// <summary>Kleopatra qui a la clé publique d'un correspondant.</summary>
    private sealed class KleopatraWith(PgpSecretKeyRing ring) : IGnuPg
    {
        public List<string> Asked { get; } = [];

        public bool HasKleopatra => true;

        public Task<IReadOnlyList<GnuPgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GnuPgKey>>([]);

        public Task<string> ExportSecretKeyAsync(string fingerprint, string? passphrase, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Jamais de clé secrète pour vérifier une signature.");

        public Task<string?> ExportPublicKeyAsync(string issuer, CancellationToken cancellationToken = default)
        {
            Asked.Add(issuer);
            return Task.FromResult<string?>(TestPgpKeys.PublicText(ring));
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
