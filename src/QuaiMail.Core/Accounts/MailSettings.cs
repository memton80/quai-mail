namespace QuaiMail.Core.Accounts;

/// <summary>D'où viennent les réglages proposés.</summary>
public enum MailSettingsSource
{
    /// <summary>Fournisseur connu de Quai Mail (table intégrée, aucune connexion).</summary>
    KnownProvider,

    /// <summary>Autoconfiguration publiée par le fournisseur lui-même (son propre domaine, en HTTPS).</summary>
    ProviderAutoconfig,

    /// <summary>Simple supposition (imap.domaine / smtp.domaine) : à vérifier.</summary>
    Guess,
}

/// <summary>Réglages proposés pour une adresse, avant vérification par une vraie connexion.</summary>
public sealed record MailSettings
{
    public required MailServer Imap { get; init; }

    public required MailServer Smtp { get; init; }

    public required string UserName { get; init; }

    public required MailSettingsSource Source { get; init; }

    /// <summary>Nom du fournisseur, s'il est connu (« Gmail », « Orange »).</summary>
    public string? ProviderName { get; init; }

    /// <summary>Le fournisseur refuse les mots de passe en IMAP : il faudra la connexion OAuth2 (prochaine étape).</summary>
    public bool RequiresOAuth { get; init; }

    /// <summary>Le fournisseur exige un « mot de passe d'application » (créé dans le compte, pas le mot de passe habituel).</summary>
    public bool RequiresAppPassword { get; init; }
}
