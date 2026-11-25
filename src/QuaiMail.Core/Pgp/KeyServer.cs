using System.Net;
using System.Net.Http.Headers;

namespace QuaiMail.Core.Pgp;

/// <summary>Serveur de clés publiques OpenPGP, pour vérifier la signature d'un mail reçu.</summary>
public interface IPgpKeyServer
{
    /// <summary>Nom affiché (« keys.openpgp.org »).</summary>
    string Name { get; }

    /// <summary>
    /// Clé publique (armurée) qui contient la clé ou sous-clé <paramref name="issuer"/> : empreinte (40 ou 64 chiffres
    /// hexadécimaux) ou identifiant (16).
    /// </summary>
    /// <returns>La clé, ou <c>null</c> si le serveur ne la connaît pas.</returns>
    /// <exception cref="HttpRequestException">Serveur injoignable ou réponse inattendue.</exception>
    Task<string?> FindAsync(string issuer, CancellationToken cancellationToken = default);
}

/// <summary>
/// keys.openpgp.org (API « VKS ») : une clé n'y porte une adresse que si son propriétaire l'a confirmée par mail, et
/// aucune adresse ne peut y être publiée par quelqu'un d'autre. Seule information envoyée : l'empreinte (ou
/// l'identifiant) de la clé qui a signé, jamais l'adresse de l'expéditeur ni rien du mail ; agent « QuaiMail/version ».
/// </summary>
public sealed class OpenPgpKeyServer : IPgpKeyServer, IDisposable
{
    private static readonly Uri Base = new("https://keys.openpgp.org/vks/v1/");

    private readonly HttpClient _http;

    public OpenPgpKeyServer(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
    }

    public string Name => "keys.openpgp.org";

    /// <summary>Adresse de la requête, construite ici à partir de chiffres hexadécimaux seulement.</summary>
    public static Uri Lookup(string issuer)
    {
        if (!issuer.All(char.IsAsciiHexDigit) || issuer.Length is not (16 or 40 or 64))
        {
            throw new ArgumentException("Empreinte ou identifiant OpenPGP attendu.", nameof(issuer));
        }

        return new Uri(Base, (issuer.Length == 16 ? "by-keyid/" : "by-fingerprint/") + issuer.ToUpperInvariant());
    }

    public async Task<string?> FindAsync(string issuer, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(Lookup(issuer), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var key = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return key.Contains("-----BEGIN PGP PUBLIC KEY BLOCK-----", StringComparison.Ordinal)
            ? key
            : throw new HttpRequestException("Réponse inattendue du serveur de clés.");
    }

    public void Dispose() => _http.Dispose();
}
