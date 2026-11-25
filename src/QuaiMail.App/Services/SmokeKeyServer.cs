using QuaiMail.Core.Pgp;

namespace QuaiMail.App.Services;

/// <summary>
/// keys.openpgp.org du test de fumée : aucune requête réseau. Il ne « publie » que la clé du correspondant du test, et
/// note chaque demande (le test vérifie qu'aucune ne part sans accord, ni deux fois pour la même clé).
/// </summary>
internal sealed class SmokeKeyServer : IPgpKeyServer
{
    public string Name => "keys.openpgp.org";

    public List<string> Requests { get; } = [];

    public Task<string?> FindAsync(string issuer, CancellationToken cancellationToken = default)
    {
        Requests.Add(issuer);
        var key = SmokePgp.Correspondent.Value.GetPublicKey();
        var known = issuer == Convert.ToHexString(key.GetFingerprint())
            || issuer == key.KeyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        return Task.FromResult(known ? SmokePgp.PublicArmored(SmokePgp.Correspondent.Value) : null);
    }
}
