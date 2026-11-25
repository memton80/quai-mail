namespace QuaiMail.Core.Mail;

/// <summary>Ce qui a empêché de parler au serveur, en termes compréhensibles (jamais de mot de passe ni de contenu).</summary>
public enum MailError
{
    /// <summary>Nom de serveur introuvable (faute de frappe, pas de réseau).</summary>
    HostNotFound,

    /// <summary>Serveur injoignable sur ce port (pare-feu, mauvais port, serveur arrêté).</summary>
    ConnectionFailed,

    /// <summary>Pas de réponse à temps.</summary>
    Timeout,

    /// <summary>Certificat TLS refusé (invalide, expiré, autre nom) : la connexion n'est pas sûre.</summary>
    Certificate,

    /// <summary>Chiffrement impossible (STARTTLS non proposé, versions TLS incompatibles).</summary>
    Tls,

    /// <summary>Identifiant ou mot de passe refusé (ou mot de passe d'application nécessaire).</summary>
    Authentication,

    /// <summary>Réponse inattendue du serveur.</summary>
    Protocol,

    /// <summary>Aucun mot de passe enregistré pour ce compte.</summary>
    MissingPassword,

    /// <summary>Connexion par le navigateur (Gmail, Outlook.com) expirée ou retirée : il faut se reconnecter.</summary>
    SignInExpired,
}

/// <summary>Échec d'une connexion à un serveur mail : le type d'erreur et le serveur, rien d'autre.</summary>
public sealed class MailConnectionException : Exception
{
    public MailConnectionException(MailError error, string server, string protocol, Exception? inner = null)
        : base($"{protocol} {server} : {error}", inner)
    {
        Error = error;
        Server = server;
        Protocol = protocol;
    }

    public MailError Error { get; }

    /// <summary>« imap.example.org:993 ».</summary>
    public string Server { get; }

    /// <summary>« IMAP » ou « SMTP ».</summary>
    public string Protocol { get; }
}
