using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace QuaiMail.App.Services;

/// <summary>
/// Test de fumée : clé OpenPGP créée pour l'occasion (jamais de vraie clé), exportée comme le ferait GnuPG, et
/// vérification de la signature comme le ferait le destinataire (clé publique seule).
/// </summary>
internal static class SmokePgp
{
    public const string Passphrase = "phrase de passe du test de fumée";

    /// <summary>Écrit la clé secrète armurée dans <paramref name="path"/> ; retourne le trousseau, pour vérifier.</summary>
    public static PgpSecretKeyRing CreateKey(string userId, string path)
    {
        var random = new SecureRandom();
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(Org.BouncyCastle.Math.BigInteger.ValueOf(65537), random, 2048, 12));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, generator.GenerateKeyPair(), DateTime.UtcNow.AddMinutes(-1));
        var flags = new PgpSignatureSubpacketGenerator();
        flags.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        var ring = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification, pair, userId, SymmetricKeyAlgorithmTag.Aes256, true, Passphrase.ToCharArray(), true,
            flags.Generate(), null, random).GenerateSecretKeyRing();
        using (var file = File.Create(path))
        using (var armor = new ArmoredOutputStream(file))
        {
            armor.Write(ring.GetEncoded());
        }

        return ring;
    }

    /// <summary>Message relu tel qu'il est parti, signature vérifiée avec la seule clé publique.</summary>
    public static bool Verify(MimeMessage message, PgpSecretKeyRing ring, string directory)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        if (MimeMessage.Load(stream).Body is not MultipartSigned signed)
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        using var context = new Recipient(directory, ring);
        return signed.Verify(context) is { Count: 1 } signatures && signatures[0].Verify();
    }

    private sealed class Recipient : GnuPGContext
    {
        public Recipient(string directory, PgpSecretKeyRing ring)
            : base(directory) =>
            Import(new PgpPublicKeyRing(ring.GetPublicKeys().OfType<PgpPublicKey>().SelectMany(k => k.GetEncoded()).ToArray()));

        protected override string GetPasswordForKey(PgpSecretKey key) => throw new OperationCanceledException();
    }
}
