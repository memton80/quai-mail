using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Services;

namespace QuaiMail.Core.Accounts.OAuth;

/// <summary>Jetons reçus du fournisseur.</summary>
/// <param name="RefreshToken">Jeton de renouvellement (gardé dans le coffre de Windows) ; parfois absent lors d'un renouvellement.</param>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Pourquoi la connexion par le navigateur n'a pas abouti.</summary>
public enum OAuthError
{
    /// <summary>Refusée par l'utilisateur sur la page du fournisseur.</summary>
    Denied,

    /// <summary>Pas de retour à temps (page fermée).</summary>
    Timeout,

    /// <summary>Réponse invalide, ou ne correspondant pas à la demande (protection contre la falsification).</summary>
    Invalid,

    /// <summary>Fournisseur injoignable.</summary>
    Network,

    /// <summary>Autorisation retirée ou expirée : il faut se reconnecter.</summary>
    Expired,
}

public sealed class OAuthException(OAuthError error, string? detail = null, Exception? inner = null)
    : Exception($"OAuth : {error}{(detail is null ? string.Empty : " (" + detail + ")")}", inner)
{
    public OAuthError Error { get; } = error;
}

/// <summary>
/// Connexion OAuth2 par le navigateur système (flux « code d'autorisation » avec PKCE, RFC 7636 / 8252) : la page
/// officielle du fournisseur s'ouvre dans le navigateur par défaut, l'utilisateur s'y connecte (Quai Mail ne voit jamais
/// son mot de passe), puis le fournisseur renvoie vers <c>http://127.0.0.1:&lt;port&gt;</c> sur cet ordinateur. Code
/// maison : quelques lignes au-dessus de <c>HttpListener</c> et <c>HttpClient</c>, communes à Google et Microsoft
/// (MSAL et Google.Apis.Auth ne couvrent chacun qu'un fournisseur). Ni jeton ni code dans les journaux.
/// </summary>
public sealed class OAuthFlow : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILauncher _launcher;
    private readonly ILogger _logger;

    public OAuthFlow(ILauncher launcher, HttpMessageHandler? handler = null, ILogger<OAuthFlow>? logger = null)
    {
        _launcher = launcher;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _logger = logger ?? NullLogger<OAuthFlow>.Instance;
    }

    /// <summary>Temps laissé pour se connecter dans le navigateur.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Ouvre la page de connexion du fournisseur et attend son retour ; retourne les jetons.</summary>
    /// <param name="loginHint">Adresse saisie, proposée sur la page du fournisseur.</param>
    public async Task<OAuthTokens> SignInAsync(OAuthProvider provider, string? loginHint, CancellationToken cancellationToken = default)
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var listening = Listen(provider);
        using var listener = listening.Listener;
        var redirect = listening.Redirect;
        var uri = AuthorizationUri(provider, redirect, state, challenge, loginHint);
        if (!_launcher.Open(uri))
        {
            throw new OAuthException(OAuthError.Invalid, "navigateur");
        }

        _logger.LogInformation("Connexion {Provider} : page ouverte dans le navigateur", provider.Name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        HttpListenerContext context;
        try
        {
            // Seul le retour du fournisseur compte (code ou erreur) ; toute autre requête (icône…) reçoit un 404.
            while (true)
            {
                context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
                var parameters = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (parameters["code"] is not null || parameters["error"] is not null)
                {
                    break;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException(OAuthError.Timeout);
        }

        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        var ok = query["error"] is null && query["code"] is { Length: > 0 } && query["state"] == state;

        // Page de retour : texte simple (aucun HTML), dans la langue de l'utilisateur.
        var answer = Encoding.UTF8.GetBytes(ok
            ? "Quai Mail : connexion réussie. Vous pouvez fermer cet onglet et revenir à Quai Mail."
            : "Quai Mail : connexion non aboutie. Vous pouvez fermer cet onglet et revenir à Quai Mail.");
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = answer.Length;
        await context.Response.OutputStream.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
        context.Response.Close();

        if (query["error"] is { } error)
        {
            throw new OAuthException(error == "access_denied" ? OAuthError.Denied : OAuthError.Invalid, error);
        }

        if (query["state"] != state)
        {
            throw new OAuthException(OAuthError.Invalid, "state");
        }

        if (query["code"] is not { Length: > 0 } code)
        {
            throw new OAuthException(OAuthError.Invalid, "code");
        }

        return await TokenAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Nouveau jeton d'accès à partir du jeton de renouvellement.</summary>
    public Task<OAuthTokens> RefreshAsync(OAuthProvider provider, string refreshToken, CancellationToken cancellationToken = default) =>
        TokenAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = provider.Scopes,
        }, cancellationToken);

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Écoute le retour sur l'ordinateur même, sur un port libre. Si Windows refuse 127.0.0.1 à un utilisateur sans
    /// droits d'administrateur (cela arrive), « localhost », toujours permis, le remplace (Google accepte les deux).
    /// </summary>
    private (HttpListener Listener, string Redirect) Listen(OAuthProvider provider)
    {
        foreach (var host in provider.RedirectHost == "localhost" ? ["localhost"] : new[] { provider.RedirectHost, "localhost" })
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://{host}:{port}/");
            }
            catch (HttpListenerException ex) when (host != "localhost")
            {
                _logger.LogInformation("Retour local sur {Host} refusé ({Code}) ; essai avec localhost", host, ex.ErrorCode);
                listener.Close();
            }
        }

        throw new OAuthException(OAuthError.Invalid, "retour local impossible");
    }

    /// <summary>Adresse de la page de connexion (adresse officielle du fournisseur, construite ici).</summary>
    public static Uri AuthorizationUri(OAuthProvider provider, string redirect, string state, string challenge, string? loginHint)
    {
        var parameters = new List<(string, string)>
        {
            ("client_id", provider.ClientId),
            ("response_type", "code"),
            ("redirect_uri", redirect),
            ("scope", provider.Scopes),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
        };
        if (provider.Auth == AccountAuth.Google)
        {
            // Jeton de renouvellement demandé explicitement chez Google.
            parameters.Add(("access_type", "offline"));
            parameters.Add(("prompt", "consent"));
        }

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            parameters.Add(("login_hint", loginHint.Trim()));
        }

        var query = string.Join('&', parameters.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return new Uri($"{provider.AuthorizationEndpoint}?{query}");
    }

    private async Task<OAuthTokens> TokenAsync(OAuthProvider provider, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        form["client_id"] = provider.ClientId;
        if (!string.IsNullOrEmpty(provider.ClientSecret))
        {
            form["client_secret"] = provider.ClientSecret;
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException(OAuthError.Network, null, ex);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(json).RootElement;
            }
            catch (JsonException ex)
            {
                throw new OAuthException(OAuthError.Invalid, "réponse", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                _logger.LogWarning("Connexion {Provider} : jeton refusé ({Error})", provider.Name, error ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                throw new OAuthException(error == "invalid_grant" ? OAuthError.Expired : OAuthError.Invalid, error);
            }

            if (!root.TryGetProperty("access_token", out var access) || access.GetString() is not { Length: > 0 } accessToken)
            {
                throw new OAuthException(OAuthError.Invalid, "access_token");
            }

            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var seconds = root.TryGetProperty("expires_in", out var x) && x.TryGetInt32(out var value) ? value : 3600;
            return new OAuthTokens(accessToken, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 60)));
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Jetons d'accès des comptes connectés par le navigateur : gardés en mémoire jusqu'à leur expiration, renouvelés à
/// partir du jeton de renouvellement du coffre de Windows (remplacé s'il change).
/// </summary>
public sealed class AccessTokens(ICredentialStore credentials, OAuthFlow flow, OAuthClients clients)
{
    private readonly Dictionary<string, OAuthTokens> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OAuthClients Clients => clients;

    /// <summary>Jeton d'accès valide pour ce compte (renouvelé si besoin).</summary>
    /// <exception cref="OAuthException">Fournisseur non configuré, autorisation retirée ou expirée.</exception>
    public async Task<string> GetAsync(Account account, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(account.Id, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.AccessToken;
            }

            var provider = clients.For(account.Auth) ?? throw new OAuthException(OAuthError.Invalid, "fournisseur non configuré");
            var refresh = credentials.GetPassword(account.Id) ?? throw new OAuthException(OAuthError.Expired);
            var tokens = await flow.RefreshAsync(provider, refresh, cancellationToken).ConfigureAwait(false);
            if (tokens.RefreshToken is { Length: > 0 } rotated && rotated != refresh)
            {
                credentials.SetPassword(account.Id, rotated);
            }

            _cache[account.Id] = tokens;
            return tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Après une connexion par le navigateur : jeton de renouvellement dans le coffre, jeton d'accès en mémoire.</summary>
    public void Store(Account account, OAuthTokens tokens)
    {
        if (tokens.RefreshToken is { Length: > 0 } refresh)
        {
            credentials.SetPassword(account.Id, refresh);
        }

        _cache[account.Id] = tokens;
    }

    public void Forget(string accountId) => _cache.Remove(accountId);
}
