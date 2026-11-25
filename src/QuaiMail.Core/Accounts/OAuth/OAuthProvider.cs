namespace QuaiMail.Core.Accounts.OAuth;

/// <summary>
/// Identifiants de Quai Mail chez Google et Microsoft (inscription décrite dans <c>docs/OAUTH.md</c>), intégrés à la
/// compilation depuis les secrets du dépôt GitHub. Vides : la connexion par le navigateur n'est pas proposée.
/// </summary>
public sealed record OAuthClients(string? GoogleClientId, string? GoogleClientSecret, string? MicrosoftClientId)
{
    public static OAuthClients None { get; } = new(null, null, null);

    public OAuthProvider? For(AccountAuth auth) => auth switch
    {
        AccountAuth.Google when !string.IsNullOrWhiteSpace(GoogleClientId) => OAuthProvider.Google(GoogleClientId, GoogleClientSecret),
        AccountAuth.Microsoft when !string.IsNullOrWhiteSpace(MicrosoftClientId) => OAuthProvider.Microsoft(MicrosoftClientId),
        _ => null,
    };
}

/// <summary>Un fournisseur OAuth2 : adresses officielles, droits demandés, identifiant de Quai Mail.</summary>
/// <param name="RedirectHost">Retour sur l'ordinateur même (Google : 127.0.0.1 ; Microsoft : localhost, comme inscrit).</param>
public sealed record OAuthProvider(
    AccountAuth Auth,
    string Name,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    string Scopes,
    string ClientId,
    string? ClientSecret,
    string RedirectHost)
{
    /// <summary>Gmail : accès IMAP / SMTP complet (le seul que Google propose pour IMAP).</summary>
    public static OAuthProvider Google(string clientId, string? clientSecret) => new(
        AccountAuth.Google,
        "Google",
        new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        new Uri("https://oauth2.googleapis.com/token"),
        "https://mail.google.com/",
        clientId,
        clientSecret,
        "127.0.0.1");

    /// <summary>Outlook.com et Microsoft 365 : IMAP et SMTP au nom de l'utilisateur, plus le renouvellement.</summary>
    public static OAuthProvider Microsoft(string clientId) => new(
        AccountAuth.Microsoft,
        "Microsoft",
        new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
        new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
        "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access",
        clientId,
        null,
        "localhost");
}
