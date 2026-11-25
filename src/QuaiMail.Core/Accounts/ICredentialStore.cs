namespace QuaiMail.Core.Accounts;

/// <summary>
/// Mots de passe des comptes, dans le coffre de Windows (<c>PasswordVault</c>, implémenté par l'application) : jamais
/// dans un fichier, jamais dans un journal.
/// </summary>
public interface ICredentialStore
{
    /// <returns>Le mot de passe du compte, ou <c>null</c> s'il n'est pas enregistré.</returns>
    string? GetPassword(string accountId);

    void SetPassword(string accountId, string password);

    /// <summary>Efface le mot de passe (compte supprimé) ; sans effet s'il n'existe pas.</summary>
    void Remove(string accountId);
}
