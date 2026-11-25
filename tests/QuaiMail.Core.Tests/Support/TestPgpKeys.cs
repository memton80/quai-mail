using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace QuaiMail.Core.Tests.Support;

/// <summary>
/// Clés OpenPGP créées pendant le test (jamais de clé privée dans le dépôt), armurées comme un export de GnuPG.
/// </summary>
public static class TestPgpKeys
{
    public static PgpSecretKeyRing Create(string userId, string passphrase, DateTime? created = null, TimeSpan? validity = null)
    {
        var random = new SecureRandom();
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(Org.BouncyCastle.Math.BigInteger.ValueOf(65537), random, 2048, 12));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, generator.GenerateKeyPair(), created ?? DateTime.UtcNow.AddMinutes(-1));
        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        if (validity is { } valid)
        {
            hashed.SetKeyExpirationTime(false, (long)valid.TotalSeconds);
        }

        var rings = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification, pair, userId, SymmetricKeyAlgorithmTag.Aes256, true, passphrase.ToCharArray(), true,
            hashed.Generate(), null, random);
        return rings.GenerateSecretKeyRing();
    }

    public static MemoryStream Armored(PgpSecretKeyRing ring) => Armored(ring.GetEncoded());

    public static MemoryStream ArmoredPublic(PgpSecretKeyRing ring)
    {
        var keys = ring.GetPublicKeys().OfType<PgpPublicKey>().ToList();
        return Armored(new PgpPublicKeyRing(keys.SelectMany(k => k.GetEncoded()).ToArray()).GetEncoded());
    }

    private static MemoryStream Armored(byte[] data)
    {
        var output = new MemoryStream();
        using (var armor = new ArmoredOutputStream(output))
        {
            armor.Write(data);
        }

        return new MemoryStream(output.ToArray());
    }

    /// <summary>Destinataire qui vérifie la signature avec la seule clé publique de l'expéditeur.</summary>
    public sealed class Verifier : GnuPGContext
    {
        public Verifier(string directory, PgpSecretKeyRing ring)
            : base(directory)
        {
            Import(new PgpPublicKeyRing(ring.GetPublicKeys().OfType<PgpPublicKey>().SelectMany(k => k.GetEncoded()).ToArray()));
        }

        protected override string GetPasswordForKey(PgpSecretKey key) => throw new OperationCanceledException();
    }
}
