using System.Text.Json.Serialization;

namespace QuaiMail.Core.Accounts;

/// <summary>Chiffrement de la connexion au serveur. Jamais de connexion en clair : il n'y a pas de valeur « aucun ».</summary>
public enum MailSecurity
{
    /// <summary>TLS dès la connexion (IMAP 993, SMTP 465).</summary>
    SslOnConnect,

    /// <summary>Connexion puis passage obligatoire en TLS (IMAP 143, SMTP 587) ; refusée si le serveur ne le propose pas.</summary>
    StartTls,
}

/// <summary>Un serveur (IMAP ou SMTP) : nom, port, chiffrement.</summary>
public sealed record MailServer
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public MailSecurity Security { get; init; } = MailSecurity.SslOnConnect;

    [JsonIgnore]
    public bool IsValid => MailAddressRules.IsValidHost(Host) && Port is > 0 and < 65536 && Enum.IsDefined(Security);
}

/// <summary>
/// Un compte mail (fichier <c>accounts.json</c>). Aucun secret ici : le mot de passe est dans le coffre de Windows
/// (<see cref="ICredentialStore"/>), sous l'identifiant du compte.
/// </summary>
public sealed record Account
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Adresse du compte (« alice@example.org »).</summary>
    public required string Address { get; init; }

    /// <summary>Nom affiché aux destinataires et dans la liste des comptes (l'adresse s'il est vide).</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Identifiant de connexion (souvent l'adresse elle-même).</summary>
    public required string UserName { get; init; }

    public required MailServer Imap { get; init; }

    public required MailServer Smtp { get; init; }

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Address : DisplayName;
}
