using System.Net;
using System.Reflection;
using System.Web;
using QuaiMail.Core.Accounts.OAuth;
using QuaiMail.Core.Services;

namespace QuaiMail.App.Services;

/// <summary>Identifiants OAuth2 intégrés à la compilation (secrets du dépôt GitHub, voir docs/OAUTH.md).</summary>
internal static class BrowserSignIn
{
    /// <summary>
    /// Connexion à Gmail et Outlook.com par le navigateur proposée dans cette version. Pas encore : boutons grisés
    /// « à venir » (décision d'Alexandre, docs/DECISIONS.md) ; le code OAuth2 de QuaiMail.Core reste testé, prêt.
    /// </summary>
    public static bool Available { get; } = false;

    public static OAuthClients Embedded { get; } = new(
        Metadata("QuaiMail.GoogleClientId"),
        Metadata("QuaiMail.GoogleClientSecret"),
        Metadata("QuaiMail.MicrosoftClientId"));

    /// <summary>Test de fumée : identifiants fictifs, fournisseur simulé.</summary>
    public static OAuthClients Smoke { get; } = new("quai-mail-smoke.apps.googleusercontent.com", "secret du test de fumée", "quai-mail-smoke-microsoft");

    private static string? Metadata(string key) =>
        typeof(BrowserSignIn).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value
            ? value.Trim()
            : null;
}

/// <summary>
/// Test de fumée : « navigateur » qui suit aussitôt le retour local vers Quai Mail, comme après une connexion réussie
/// chez le fournisseur. Rien n'est ouvert, aucune requête ne sort de l'ordinateur.
/// </summary>
internal sealed class SmokeBrowser : ILauncher
{
    public List<Uri> Opened { get; } = [];

    public bool Open(Uri uri)
    {
        Opened.Add(uri);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var back = $"{query["redirect_uri"]}?code=code-du-test-de-fumee&state={Uri.EscapeDataString(query["state"] ?? string.Empty)}";
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            await http.GetAsync(new Uri(back));
        });
        return true;
    }
}

/// <summary>Test de fumée : point d'émission des jetons simulé (en mémoire).</summary>
internal sealed class SmokeTokenEndpoint : HttpMessageHandler
{
    public const string RefreshToken = "jeton de renouvellement du test de fumée";

    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
        Requests.Add(form["grant_type"] ?? string.Empty);
        var ok = form["grant_type"] == "refresh_token" || form["code"] == "code-du-test-de-fumee" && form["code_verifier"] is { Length: >= 43 };
        return ok
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "access_token": "{{SmokeMailService.AccessToken}}", "refresh_token": "{{RefreshToken}}", "expires_in": 3600, "token_type": "Bearer" }"""),
            }
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{ "error": "invalid_grant" }""") };
    }
}
