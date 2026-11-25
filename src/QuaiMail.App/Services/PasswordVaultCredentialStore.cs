using QuaiMail.Core.Accounts;
using Windows.Security.Credentials;

namespace QuaiMail.App.Services;

/// <summary>
/// Mots de passe des comptes dans le coffre de Windows (<see cref="PasswordVault"/>, visible dans le Gestionnaire
/// d'identification), sous l'identifiant interne du compte (repris de Quai). Rien n'est écrit dans les fichiers de
/// l'application.
/// </summary>
internal sealed class PasswordVaultCredentialStore : ICredentialStore
{
    /// <summary>Ressource des mots de passe de Quai Mail.</summary>
    public const string DefaultResource = "QuaiMail";

    /// <summary>Ressource du test de fumée : jamais celle des vrais comptes.</summary>
    public const string SmokeResource = "QuaiMail-test-de-fumee";

    private const int ElementNotFound = unchecked((int)0x80070490);

    private readonly Lazy<PasswordVault> _vault = new(() => new PasswordVault());
    private readonly string _resource;

    public PasswordVaultCredentialStore(string resource = DefaultResource)
    {
        _resource = resource;
    }

    public string? GetPassword(string accountId)
    {
        try
        {
            var credential = _vault.Value.Retrieve(_resource, accountId);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            return null;
        }
    }

    public void SetPassword(string accountId, string password) =>
        _vault.Value.Add(new PasswordCredential(_resource, accountId, password));

    public void Remove(string accountId)
    {
        try
        {
            _vault.Value.Remove(_vault.Value.Retrieve(_resource, accountId));
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            // Déjà absent.
        }
    }

    /// <summary>Supprime tous les mots de passe de cette ressource (désinstallation avec effacement des données).</summary>
    public static void RemoveAll(PasswordVault vault, string resource)
    {
        IReadOnlyList<PasswordCredential> credentials;
        try
        {
            credentials = vault.FindAllByResource(resource);
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            return;
        }

        foreach (var credential in credentials)
        {
            vault.Remove(credential);
        }
    }
}
