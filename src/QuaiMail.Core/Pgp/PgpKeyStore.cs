using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.Pgp;

/// <summary>Une clé secrète OpenPGP importée, capable de signer.</summary>
/// <param name="Fingerprint">Empreinte de la clé principale (40 chiffres hexadécimaux, ou 64 en version 5 / 6).</param>
/// <param name="UserId">Identité principale (« Nom &lt;adresse&gt; »).</param>
/// <param name="NeedsPassphrase">La clé est protégée par une phrase de passe (cas normal).</param>
public sealed record PgpKeyInfo(
    string Fingerprint,
    string UserId,
    IReadOnlyList<string> Addresses,
    DateTimeOffset Created,
    DateTimeOffset? Expires,
    string Algorithm,
    bool NeedsPassphrase)
{
    /// <summary>Empreinte lisible : groupes de 4, comme Kleopatra et <c>gpg --fingerprint</c>.</summary>
    public string DisplayFingerprint => string.Join(' ', Enumerable.Range(0, Fingerprint.Length / 4).Select(i => Fingerprint.Substring(i * 4, 4)));

    public bool IsExpired(DateTimeOffset now) => Expires is { } end && end <= now;
}

public enum PgpError
{
    /// <summary>Fichier illisible (ni clé OpenPGP armurée, ni binaire).</summary>
    InvalidFile,

    /// <summary>Le fichier ne contient que la clé publique : il faut exporter la clé secrète.</summary>
    NoSecretKey,

    /// <summary>Aucune clé (ou sous-clé) capable de signer, valide et présente (pas un simple talon de carte à puce).</summary>
    NoSigningKey,

    /// <summary>Phrase de passe refusée.</summary>
    WrongPassphrase,

    /// <summary>Clé expirée ou révoquée.</summary>
    Expired,
}

public sealed class PgpKeyException(PgpError error, Exception? inner = null) : Exception("OpenPGP : " + error, inner)
{
    public PgpError Error { get; } = error;
}

/// <summary>
/// Signature OpenPGP des mails (PGP/MIME, RFC 3156) par MimeKit et BouncyCastle, sans GnuPG à installer. Les clés
/// secrètes exportées de GnuPG ou de Kleopatra sont importées dans un trousseau propre à Quai Mail
/// (<see cref="AppPaths.PgpDirectory"/>), toujours protégées par leur phrase de passe. La phrase de passe n'est
/// jamais écrite sur disque : gardée en mémoire jusqu'à la fermeture (ou « Oublier »), comme l'agent de GnuPG.
/// </summary>
public sealed class PgpKeyStore : IDisposable
{
    private readonly KeyringContext _context;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _passphrases = [];
    private readonly Lock _gate = new();

    static PgpKeyStore() => CryptographyModule.Initialize();

    public PgpKeyStore(AppPaths paths, ILogger<PgpKeyStore>? logger = null)
    {
        Directory.CreateDirectory(paths.PgpDirectory);
        _context = new KeyringContext(paths.PgpDirectory);
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Le trousseau a changé (clé importée ou retirée).</summary>
    public event EventHandler? Changed;

    /// <summary>Clés capables de signer, de la plus récente à la plus ancienne.</summary>
    public IReadOnlyList<PgpKeyInfo> Keys
    {
        get
        {
            lock (_gate)
            {
                return [.. _context.SecretKeyRingBundle.GetKeyRings().Select(Describe).OfType<PgpKeyInfo>().OrderByDescending(k => k.Created)];
            }
        }
    }

    /// <summary>
    /// Importe les clés secrètes d'un fichier exporté (<c>gpg --export-secret-keys --armor</c>, ou « Sauvegarder les
    /// clés secrètes » de Kleopatra). Les clés publiques seules sont refusées (elles ne peuvent pas signer).
    /// </summary>
    /// <returns>Les clés importées capables de signer.</returns>
    public IReadOnlyList<PgpKeyInfo> Import(Stream stream)
    {
        var rings = new List<PgpSecretKeyRing>();
        var publicOnly = false;
        try
        {
            using var decoder = PgpUtilities.GetDecoderStream(stream);
            var factory = new PgpObjectFactory(decoder);
            while (factory.NextPgpObject() is { } item)
            {
                switch (item)
                {
                    case PgpSecretKeyRing ring:
                        rings.Add(ring);
                        break;
                    case PgpPublicKeyRing:
                        publicOnly = true;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or PgpException or ArgumentException or InvalidOperationException)
        {
            throw new PgpKeyException(rings.Count == 0 ? PgpError.InvalidFile : PgpError.NoSigningKey, ex);
        }

        if (rings.Count == 0)
        {
            throw new PgpKeyException(publicOnly ? PgpError.NoSecretKey : PgpError.InvalidFile);
        }

        var usable = rings.Select(r => (Ring: r, Info: Describe(r))).Where(r => r.Info is not null).ToList();
        if (usable.Count == 0)
        {
            throw new PgpKeyException(PgpError.NoSigningKey);
        }

        lock (_gate)
        {
            foreach (var (ring, _) in usable)
            {
                // Une nouvelle version de la même clé (prolongée, nouvelles identités) remplace l'ancienne.
                if (_context.SecretKeyRingBundle.GetSecretKeyRing(ring.GetPublicKey().KeyId) is { } existing)
                {
                    _context.Delete(existing);
                }

                _context.Import(ring);
            }
        }

        _logger.LogInformation("Clé OpenPGP importée ({Count})", usable.Count);
        Changed?.Invoke(this, EventArgs.Empty);
        return [.. usable.Select(r => r.Info!)];
    }

    /// <summary>Retire une clé du trousseau de Quai Mail (l'original, dans GnuPG ou Kleopatra, n'est pas touché).</summary>
    public bool Remove(string fingerprint)
    {
        lock (_gate)
        {
            if (FindRing(fingerprint) is not { } ring)
            {
                return false;
            }

            _context.Delete(ring);
            _passphrases.Remove(fingerprint);
        }

        _logger.LogInformation("Clé OpenPGP retirée");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Partie publique d'une de vos clés qui contient la clé ou sous-clé <paramref name="keyId"/> (vérifier un mail que
    /// vous avez signé), ou <c>null</c>.
    /// </summary>
    public PgpPublicKeyRing? FindPublicRing(long keyId)
    {
        lock (_gate)
        {
            return _context.SecretKeyRingBundle.GetSecretKeyRing(keyId) is { } ring
                ? new PgpPublicKeyRing(ring.GetPublicKeys().OfType<PgpPublicKey>().SelectMany(k => k.GetEncoded()).ToArray())
                : null;
        }
    }

    /// <summary>Clé valide pour signer les mails de cette adresse (la plus récente), ou <c>null</c>.</summary>
    public PgpKeyInfo? FindForAddress(string address, DateTimeOffset now) =>
        Keys.FirstOrDefault(k => !k.IsExpired(now) && k.Addresses.Contains(address.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Phrase de passe déjà donnée pendant cette session.</summary>
    public string? RememberedPassphrase(string fingerprint)
    {
        lock (_gate)
        {
            return _passphrases.GetValueOrDefault(fingerprint);
        }
    }

    /// <summary>Oublie toutes les phrases de passe gardées en mémoire.</summary>
    public void ForgetPassphrases()
    {
        lock (_gate)
        {
            _passphrases.Clear();
        }
    }

    /// <summary>
    /// Signe le corps du message (PGP/MIME, SHA-256) : le corps devient un <c>multipart/signed</c>. La phrase de passe
    /// est vérifiée avant, puis gardée en mémoire pour la session.
    /// </summary>
    /// <exception cref="PgpKeyException">Clé absente ou expirée, phrase de passe refusée.</exception>
    public void Sign(MimeMessage message, PgpKeyInfo key, string? passphrase, DateTimeOffset now)
    {
        if (key.IsExpired(now))
        {
            throw new PgpKeyException(PgpError.Expired);
        }

        lock (_gate)
        {
            if (FindRing(key.Fingerprint) is not { } ring || SigningKey(ring, now) is not { } secret)
            {
                throw new PgpKeyException(PgpError.NoSigningKey);
            }

            Unlock(secret, passphrase);
            _context.Unlocked = (secret.KeyId, ForMimeKit(passphrase ?? string.Empty));
            try
            {
                message.Body = MultipartSigned.Create(_context, secret, DigestAlgorithm.Sha256, message.Body);
            }
            finally
            {
                _context.Unlocked = null;
            }

            if (key.NeedsPassphrase && passphrase is not null)
            {
                _passphrases[key.Fingerprint] = passphrase;
            }
        }
    }

    public void Dispose() => _context.Dispose();

    /// <summary>Clé privée déverrouillée ; phrase de passe en UTF-8, comme GnuPG (accents compris).</summary>
    private static PgpPrivateKey Unlock(PgpSecretKey secret, string? passphrase)
    {
        try
        {
            return secret.ExtractPrivateKeyUtf8((passphrase ?? string.Empty).ToCharArray())
                ?? throw new PgpKeyException(PgpError.NoSigningKey);
        }
        catch (PgpException ex)
        {
            throw new PgpKeyException(PgpError.WrongPassphrase, ex);
        }
    }

    /// <summary>
    /// MimeKit déverrouille la clé avec <c>ExtractPrivateKey(char[])</c>, qui garde un octet par caractère ; GnuPG
    /// chiffre avec la phrase en UTF-8. Chaque octet UTF-8 est donc passé comme un caractère : identique pour une
    /// phrase en ASCII, et juste pour les accents (vérifié par les tests, et contre une clé créée par gpg).
    /// </summary>
    private static string ForMimeKit(string passphrase) =>
        new([.. System.Text.Encoding.UTF8.GetBytes(passphrase).Select(b => (char)b)]);

    private PgpSecretKeyRing? FindRing(string fingerprint) =>
        _context.SecretKeyRingBundle.GetKeyRings().FirstOrDefault(r => Fingerprint(r.GetPublicKey()) == fingerprint);

    private static string Fingerprint(PgpPublicKey key) => Convert.ToHexString(key.GetFingerprint());

    private static PgpKeyInfo? Describe(PgpSecretKeyRing ring)
    {
        var master = ring.GetPublicKey();
        if (master.HasRevocation() || SigningKey(ring, null) is not { } signing)
        {
            return null;
        }

        var userIds = PgpIdentity.UserIds(master).ToList();
        var addresses = userIds
            .Select(id => MailboxAddress.TryParse(id, out var mailbox) ? mailbox.Address : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var created = new DateTimeOffset(DateTime.SpecifyKind(master.CreationTime, DateTimeKind.Utc));
        var valid = master.GetValidSeconds();
        return new PgpKeyInfo(
            Fingerprint(master),
            userIds.FirstOrDefault() ?? string.Empty,
            addresses,
            created,
            valid > 0 ? created.AddSeconds(valid) : null,
            AlgorithmName(signing.PublicKey),
            signing.KeyEncryptionAlgorithm != SymmetricKeyAlgorithmTag.Null);
    }

    /// <summary>
    /// Clé qui signe : la clé principale si elle le peut, sinon une sous-clé de signature (la plus récente) ; jamais un
    /// talon vide (clé gardée sur une carte à puce), ni une clé expirée ou révoquée.
    /// </summary>
    private static PgpSecretKey? SigningKey(PgpSecretKeyRing ring, DateTimeOffset? now)
    {
        var master = ring.GetPublicKey();
        return ring.GetSecretKeys()
            .Where(k => k.IsSigningKey && !k.IsPrivateKeyEmpty && !k.PublicKey.HasRevocation())
            .Where(k => now is null || !Expired(k.PublicKey, now.Value))
            .Where(k => CanSign(k.PublicKey, k.IsMasterKey))
            .OrderByDescending(k => k.IsMasterKey)
            .ThenByDescending(k => k.PublicKey.CreationTime)
            .FirstOrDefault();
    }

    private static bool Expired(PgpPublicKey key, DateTimeOffset now) =>
        key.GetValidSeconds() > 0 && new DateTimeOffset(DateTime.SpecifyKind(key.CreationTime, DateTimeKind.Utc)).AddSeconds(key.GetValidSeconds()) <= now;

    /// <summary>Drapeaux de la clé (« S ») : une sous-clé de chiffrement RSA ne signe pas, même si son algorithme le peut.</summary>
    private static bool CanSign(PgpPublicKey key, bool isMaster)
    {
        var flags = key.GetSignatures()
            .OfType<PgpSignature>()
            .Where(s => s.HasSubpackets && s.GetHashedSubPackets().HasSubpacket(SignatureSubpacketTag.KeyFlags))
            .Select(s => s.GetHashedSubPackets().GetKeyFlags())
            .ToList();
        if (flags.Count == 0)
        {
            // Clé ancienne sans drapeaux : la clé principale signe, une sous-clé non.
            return isMaster;
        }

        return flags.Any(f => (f & PgpKeyFlags.CanSign) != 0);
    }

    private static string AlgorithmName(PgpPublicKey key) => key.Algorithm switch
    {
        PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign => string.Create(CultureInfo.InvariantCulture, $"RSA {key.BitStrength}"),
        PublicKeyAlgorithmTag.EdDsa_Legacy or PublicKeyAlgorithmTag.Ed25519 => "Ed25519",
        PublicKeyAlgorithmTag.Ed448 => "Ed448",
        PublicKeyAlgorithmTag.ECDsa => string.Create(CultureInfo.InvariantCulture, $"ECDSA {key.BitStrength}"),
        PublicKeyAlgorithmTag.Dsa => "DSA",
        var other => other.ToString(),
    };

    /// <summary>Trousseau de MimeKit dans le dossier de Quai Mail ; la phrase de passe, déjà vérifiée, lui est passée.</summary>
    private sealed class KeyringContext(string directory) : GnuPGContext(directory)
    {
        public (long KeyId, string Passphrase)? Unlocked { get; set; }

        protected override string GetPasswordForKey(PgpSecretKey key) =>
            Unlocked is { } unlocked && unlocked.KeyId == key.KeyId ? unlocked.Passphrase : throw new OperationCanceledException();
    }
}
