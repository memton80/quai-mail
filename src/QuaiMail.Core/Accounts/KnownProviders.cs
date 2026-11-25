namespace QuaiMail.Core.Accounts;

/// <summary>
/// Réglages de quelques grands fournisseurs, repris de leur documentation publique : trouvés sans aucune connexion.
/// Les autres domaines passent par l'autoconfiguration publiée par le fournisseur, puis par la saisie manuelle.
/// </summary>
public static class KnownProviders
{
    private sealed record Provider(
        string Name,
        string[] Domains,
        string ImapHost,
        int ImapPort,
        MailSecurity ImapSecurity,
        string SmtpHost,
        int SmtpPort,
        MailSecurity SmtpSecurity,
        bool RequiresOAuth = false,
        bool RequiresAppPassword = false);

    private static readonly Provider[] Providers =
    [
        new("Gmail", ["gmail.com", "googlemail.com"],
            "imap.gmail.com", 993, MailSecurity.SslOnConnect, "smtp.gmail.com", 465, MailSecurity.SslOnConnect, RequiresAppPassword: true),
        new("Outlook.com", ["outlook.com", "outlook.fr", "hotmail.com", "hotmail.fr", "live.com", "live.fr", "msn.com"],
            "outlook.office365.com", 993, MailSecurity.SslOnConnect, "smtp-mail.outlook.com", 587, MailSecurity.StartTls, RequiresOAuth: true),
        new("Yahoo", ["yahoo.com", "yahoo.fr", "ymail.com"],
            "imap.mail.yahoo.com", 993, MailSecurity.SslOnConnect, "smtp.mail.yahoo.com", 465, MailSecurity.SslOnConnect, RequiresAppPassword: true),
        new("iCloud", ["icloud.com", "me.com", "mac.com"],
            "imap.mail.me.com", 993, MailSecurity.SslOnConnect, "smtp.mail.me.com", 587, MailSecurity.StartTls, RequiresAppPassword: true),
        new("AOL", ["aol.com", "aol.fr"],
            "imap.aol.com", 993, MailSecurity.SslOnConnect, "smtp.aol.com", 465, MailSecurity.SslOnConnect, RequiresAppPassword: true),
        new("Fastmail", ["fastmail.com", "fastmail.fm"],
            "imap.fastmail.com", 993, MailSecurity.SslOnConnect, "smtp.fastmail.com", 465, MailSecurity.SslOnConnect, RequiresAppPassword: true),
        new("Orange", ["orange.fr", "wanadoo.fr"],
            "imap.orange.fr", 993, MailSecurity.SslOnConnect, "smtp.orange.fr", 465, MailSecurity.SslOnConnect),
        new("Free", ["free.fr"],
            "imap.free.fr", 993, MailSecurity.SslOnConnect, "smtp.free.fr", 465, MailSecurity.SslOnConnect),
        new("SFR", ["sfr.fr", "neuf.fr"],
            "imap.sfr.fr", 993, MailSecurity.SslOnConnect, "smtp.sfr.fr", 465, MailSecurity.SslOnConnect),
        new("La Poste", ["laposte.net"],
            "imap.laposte.net", 993, MailSecurity.SslOnConnect, "smtp.laposte.net", 465, MailSecurity.SslOnConnect),
    ];

    /// <returns>Les réglages du fournisseur de cette adresse, ou <c>null</c> s'il n'est pas dans la table.</returns>
    public static MailSettings? Find(string address)
    {
        if (!MailAddressRules.TryGetDomain(address, out var domain))
        {
            return null;
        }

        var provider = Providers.FirstOrDefault(p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));
        return provider is null
            ? null
            : new MailSettings
            {
                Imap = new MailServer { Host = provider.ImapHost, Port = provider.ImapPort, Security = provider.ImapSecurity },
                Smtp = new MailServer { Host = provider.SmtpHost, Port = provider.SmtpPort, Security = provider.SmtpSecurity },
                UserName = address.Trim(),
                Source = MailSettingsSource.KnownProvider,
                ProviderName = provider.Name,
                RequiresOAuth = provider.RequiresOAuth,
                RequiresAppPassword = provider.RequiresAppPassword,
            };
    }

    /// <summary>Supposition habituelle (imap.domaine:993, smtp.domaine:465), à faire vérifier par une connexion.</summary>
    public static MailSettings? Guess(string address)
    {
        if (!MailAddressRules.TryGetDomain(address, out var domain))
        {
            return null;
        }

        return new MailSettings
        {
            Imap = new MailServer { Host = "imap." + domain, Port = 993, Security = MailSecurity.SslOnConnect },
            Smtp = new MailServer { Host = "smtp." + domain, Port = 465, Security = MailSecurity.SslOnConnect },
            UserName = address.Trim(),
            Source = MailSettingsSource.Guess,
        };
    }
}
