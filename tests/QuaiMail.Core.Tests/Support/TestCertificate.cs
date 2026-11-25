using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuaiMail.Core.Tests.Support;

/// <summary>
/// Certificat auto-signé « localhost », créé pour les tests : les faux serveurs parlent TLS comme les vrais. Le service
/// de mail ne l'accepte que si le test l'épingle explicitement (<see cref="Pinned"/>) ; sa validation normale le refuse.
/// </summary>
public static class TestCertificate
{
    private static readonly Lazy<X509Certificate2> Shared = new(Create);

    public static X509Certificate2 Server => Shared.Value;

    /// <summary>Accepte exactement ce certificat-là, et rien d'autre.</summary>
    public static bool Pinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString() == Server.GetCertHashString();

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));

        // Windows (SChannel) veut une clé privée rechargée depuis un PFX, pas une clé éphémère.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}
