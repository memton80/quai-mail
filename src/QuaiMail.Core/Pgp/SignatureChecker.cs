using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;
using QuaiMail.Core.Reading;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.Pgp;

/// <summary>Verdict sur la signature OpenPGP d'un mail reçu.</summary>
public enum SignatureState
{
    /// <summary>La clé qui a signé est introuvable : impossible de savoir qui a signé.</summary>
    UnknownKey,

    /// <summary>Signature valide, faite par une clé qui porte l'adresse de l'expéditeur.</summary>
    Valid,

    /// <summary>Signature valide, mais la clé ne porte pas l'adresse de l'expéditeur (ou aucune adresse confirmée).</summary>
    OtherIdentity,

    /// <summary>Signature valide, mais faite avec une clé révoquée, ou déjà expirée au moment de signer.</summary>
    KeyNotValid,

    /// <summary>Signature fausse ou illisible : le mail a été modifié après sa signature, ou elle a été fabriquée.</summary>
    Invalid,
}

/// <summary>Où la clé qui a signé a été trouvée.</summary>
public enum SignatureKeySource
{
    /// <summary>Une de vos clés (mail que vous avez signé).</summary>
    OwnKey,

    /// <summary>Le trousseau de Kleopatra (GnuPG) : une clé que vous y avez mise.</summary>
    Kleopatra,

    /// <summary>Le serveur de clés : ses adresses ont été confirmées par mail par le propriétaire de la clé.</summary>
    KeyServer,
}

/// <param name="Issuer">Empreinte (ou identifiant) de la clé qui a signé, lue dans la signature.</param>
/// <param name="Fingerprint">Empreinte de la clé principale trouvée.</param>
/// <param name="SignerUserId">Identité de la clé : celle qui porte l'adresse de l'expéditeur s'il y en a une.</param>
/// <param name="LookedUp">Le serveur de clés a été consulté (ou la clé y avait déjà été trouvée).</param>
/// <param name="ServerUnreachable">Le serveur de clés n'a pas répondu.</param>
public sealed record SignatureReport(
    SignatureState State,
    string Issuer,
    string? Fingerprint,
    string? SignerUserId,
    SignatureKeySource? Source,
    DateTimeOffset? SignedAt,
    bool LookedUp,
    bool ServerUnreachable)
{
    /// <summary>Empreinte lisible (groupes de 4), de la clé trouvée ou, à défaut, lue dans la signature.</summary>
    public string DisplayFingerprint => Group(Fingerprint ?? Issuer);

    private static string Group(string hex) => string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
}

/// <summary>
/// Vérifie la signature OpenPGP (PGP/MIME) d'un mail reçu. La clé qui a signé est cherchée parmi vos clés, puis dans
/// Kleopatra (GnuPG, sans phrase de passe), puis parmi les clés déjà reçues du serveur de clés, puis, si c'est permis,
/// sur le serveur (empreinte seulement). La vérification elle-même est faite par MimeKit, sur le mail tel que reçu.
/// </summary>
public sealed class SignatureChecker
{
    /// <summary>Une clé reçue du serveur est redemandée après ce délai (révocation, prolongation).</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly string _cacheDirectory;
    private readonly PgpKeyStore _own;
    private readonly IGnuPgLocator? _gnupg;
    private readonly IPgpKeyServer? _server;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _cacheGate = new();

    public SignatureChecker(
        AppPaths paths,
        PgpKeyStore own,
        IGnuPgLocator? gnupg,
        IPgpKeyServer? server,
        TimeProvider? time = null,
        ILogger<SignatureChecker>? logger = null)
    {
        _cacheDirectory = paths.PgpKeyServerCache;
        _own = own;
        _gnupg = gnupg;
        _server = server;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Nom du serveur de clés (« keys.openpgp.org »), ou <c>null</c> s'il n'y en a pas.</summary>
    public string? KeyServerName => _server?.Name;

    /// <param name="useKeyServer">Le serveur de clés peut être consulté (réglage, ou bouton du bandeau).</param>
    public async Task<SignatureReport> CheckAsync(MessageContent content, bool useKeyServer, CancellationToken cancellationToken = default)
    {
        var signed = content.PgpSigned ?? throw new ArgumentException("Mail non signé en OpenPGP.", nameof(content));
        if (ReadSignature(signed) is not var (keyId, issuer, signedAt))
        {
            return new SignatureReport(SignatureState.Invalid, string.Empty, null, null, null, null, false, false);
        }

        var source = SignatureKeySource.OwnKey;
        var ring = _own.FindPublicRing(keyId);
        if (ring is null && FindGnuPg() is { } gnupg)
        {
            source = SignatureKeySource.Kleopatra;
            try
            {
                ring = Parse(await gnupg.ExportPublicKeyAsync(issuer, cancellationToken).ConfigureAwait(false), keyId);
            }
            catch (GnuPgException ex)
            {
                _logger.LogWarning("Clé publique non lue dans GnuPG ({Error})", ex.Error);
            }
        }

        var lookedUp = false;
        var unreachable = false;
        if (ring is null)
        {
            source = SignatureKeySource.KeyServer;
            var (cached, fresh) = FromCache(keyId);
            ring = cached;
            lookedUp = cached is not null;
            if ((cached is null || !fresh) && useKeyServer && _server is not null)
            {
                lookedUp = true;
                try
                {
                    if (Parse(await _server.FindAsync(issuer, cancellationToken).ConfigureAwait(false), keyId) is { } found)
                    {
                        ring = found;
                        SaveToCache(found);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // Hors connexion : une clé déjà reçue sert encore ; sinon la clé reste inconnue.
                    _logger.LogWarning("Serveur de clés injoignable ({Error})", ex.GetType().Name);
                    unreachable = cached is null;
                }
            }
        }

        if (ring is null)
        {
            return new SignatureReport(SignatureState.UnknownKey, issuer, null, null, null, signedAt, lookedUp, unreachable);
        }

        var userIds = PgpIdentity.UserIds(ring.GetPublicKey());
        var from = content.FromAddress.Trim().ToLowerInvariant();
        var matching = userIds.FirstOrDefault(u => PgpIdentity.Addresses([u]).Contains(from));
        var state = Verify(signed, ring) switch
        {
            false => SignatureState.Invalid,
            true when !IsUsable(ring, keyId, signedAt) => SignatureState.KeyNotValid,
            true when matching is not null => SignatureState.Valid,
            true => SignatureState.OtherIdentity,
        };
        var fingerprint = Convert.ToHexString(ring.GetPublicKey().GetFingerprint());
        return new SignatureReport(state, issuer, fingerprint, matching ?? userIds.FirstOrDefault(), source, signedAt, lookedUp, false);
    }

    private IGnuPg? FindGnuPg()
    {
        try
        {
            return _gnupg?.Find();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Identifiant de la clé qui a signé, son empreinte si la signature la donne (GnuPG 2.2 et suivants, sinon
    /// l'identifiant), et l'heure de la signature.
    /// </summary>
    private static (long KeyId, string Issuer, DateTimeOffset SignedAt)? ReadSignature(MultipartSigned signed)
    {
        try
        {
            if (signed[1] is not MimePart { Content: { } body })
            {
                return null;
            }

            using var data = new MemoryStream();
            body.DecodeTo(data);
            data.Position = 0;
            using var decoder = PgpUtilities.GetDecoderStream(data);
            var factory = new PgpObjectFactory(decoder);
            while (factory.NextPgpObject() is { } item)
            {
                if (item is PgpSignatureList { Count: > 0 } list)
                {
                    var signature = list[0];
                    var fingerprint = signature.GetHashedSubPackets()?.GetIssuerFingerprint()?.GetFingerprint();
                    var keyId = signature.KeyId;
                    if (keyId == 0 && fingerprint is { Length: 20 })
                    {
                        keyId = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(fingerprint.AsSpan(12));
                    }

                    var issuer = fingerprint is { Length: 20 or 32 } ? Convert.ToHexString(fingerprint) : keyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
                    return (keyId, issuer, new DateTimeOffset(DateTime.SpecifyKind(signature.CreationTime, DateTimeKind.Utc)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or PgpException or ArgumentException or InvalidOperationException)
        {
            // Signature illisible : traitée comme fausse.
        }

        return null;
    }

    /// <summary>Signature vérifiée par MimeKit, avec cette seule clé publique.</summary>
    private static bool Verify(MultipartSigned signed, PgpPublicKeyRing ring)
    {
        try
        {
            using var context = new VerifyingContext(ring);
            var signatures = signed.Verify(context);
            return signatures.Count > 0 && signatures[0].Verify();
        }
        catch (Exception ex) when (ex is DigitalSignatureVerifyException or PgpException or IOException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Clé (principale et sous-clé qui a signé) ni révoquée, ni expirée au moment de la signature.</summary>
    private static bool IsUsable(PgpPublicKeyRing ring, long keyId, DateTimeOffset signedAt)
    {
        foreach (var key in new[] { ring.GetPublicKey(), ring.GetPublicKey(keyId) }.OfType<PgpPublicKey>())
        {
            if (key.HasRevocation())
            {
                return false;
            }

            var seconds = key.GetValidSeconds();
            if (seconds > 0 && new DateTimeOffset(DateTime.SpecifyKind(key.CreationTime, DateTimeKind.Utc)).AddSeconds(seconds) <= signedAt)
            {
                return false;
            }
        }

        return true;
    }

    private static PgpPublicKeyRing? Parse(string? armored, long keyId)
    {
        if (armored is null)
        {
            return null;
        }

        try
        {
            using var decoder = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(armored)));
            var factory = new PgpObjectFactory(decoder);
            while (factory.NextPgpObject() is { } item)
            {
                if (item is PgpPublicKeyRing ring && ring.GetPublicKey(keyId) is not null)
                {
                    return ring;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or PgpException or ArgumentException or InvalidOperationException)
        {
            // Clé illisible : comme absente.
        }

        return null;
    }

    /// <summary>Clé déjà reçue du serveur (un fichier par clé, nommé par son empreinte) ; fraîche si reçue il y a moins de 7 jours.</summary>
    private (PgpPublicKeyRing? Ring, bool Fresh) FromCache(long keyId)
    {
        lock (_cacheGate)
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return (null, false);
            }

            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.asc"))
            {
                try
                {
                    if (Parse(File.ReadAllText(file), keyId) is { } ring)
                    {
                        return (ring, _time.GetUtcNow() - File.GetLastWriteTimeUtc(file) < CacheLifetime);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Fichier illisible : ignoré, la clé sera redemandée.
                }
            }

            return (null, false);
        }
    }

    private void SaveToCache(PgpPublicKeyRing ring)
    {
        try
        {
            using var output = new MemoryStream();
            using (var armor = new Org.BouncyCastle.Bcpg.ArmoredOutputStream(output))
            {
                ring.Encode(armor);
            }

            lock (_cacheGate)
            {
                Directory.CreateDirectory(_cacheDirectory);
                var path = Path.Combine(_cacheDirectory, Convert.ToHexString(ring.GetPublicKey().GetFingerprint()) + ".asc");
                AtomicFile.Write(path, output.ToArray());
                File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Clé publique non gardée ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Contexte OpenPGP en mémoire, réduit à une clé publique : il ne sert qu'à vérifier.</summary>
    private sealed class VerifyingContext(PgpPublicKeyRing ring) : OpenPgpContext
    {
        protected override PgpPublicKeyRing GetPublicKeyRing(long keyId, CancellationToken cancellationToken = default) =>
            ring.GetPublicKey(keyId) is null ? null! : ring;

        protected override Task<PgpPublicKeyRing?> GetPublicKeyRingAsync(long keyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PgpPublicKeyRing?>(GetPublicKeyRing(keyId, cancellationToken));

        public override bool CanEncrypt(MailboxAddress mailbox, CancellationToken cancellationToken = default) => false;

        public override bool CanSign(MailboxAddress signer, CancellationToken cancellationToken = default) => false;

        public override MimePart Export(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<MimePart> ExportAsync(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override void Export(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task ExportAsync(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override IList<PgpPublicKey> GetPublicKeys(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override PgpSecretKey GetSecretKey(long keyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override PgpSecretKey GetSigningKey(MailboxAddress mailbox, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override void Import(PgpPublicKeyRing keyring, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override void Import(PgpPublicKeyRingBundle bundle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override void Import(PgpSecretKeyRing keyring, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override void Import(PgpSecretKeyRingBundle bundle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override string GetPasswordForKey(PgpSecretKey key) => throw new NotSupportedException();
    }
}

/// <summary>Identités OpenPGP (« Nom &lt;adresse&gt; ») et leurs adresses.</summary>
public static class PgpIdentity
{
    /// <summary>
    /// Identités de la clé, lues en UTF-8 comme le veut OpenPGP (<c>GetUserIds</c> de BouncyCastle les lit octet par
    /// octet : « Léa » y devient « LÃ©a »).
    /// </summary>
    public static IReadOnlyList<string> UserIds(PgpPublicKey key) =>
        [.. key.GetRawUserIds().Select(raw => Encoding.UTF8.GetString(raw))];

    /// <summary>Adresses des identités, en minuscules.</summary>
    public static IReadOnlyList<string> Addresses(IEnumerable<string> userIds) =>
        [.. userIds
            .Select(u => u.LastIndexOf('<') is var start and >= 0 && u.IndexOf('>', start) is var end and > 0 ? u[(start + 1)..end] : u)
            .Where(a => a.Contains('@', StringComparison.Ordinal))
            .Select(a => a.Trim().ToLowerInvariant())
            .Distinct()];
}
