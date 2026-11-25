using Windows.Security.Credentials;

namespace QuaiMail.App.Services;

/// <summary>
/// <c>QuaiMail.exe --purge-secrets</c> (repris de Quai) : efface du coffre de Windows tous les mots de passe de Quai
/// Mail, sans ouvrir de fenêtre. Appelé par le désinstallateur quand l'utilisateur choisit de supprimer aussi ses données.
/// </summary>
internal static class SecretPurge
{
    private const string Switch = "--purge-secrets";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Retourne 0 si le coffre ne contient plus aucun mot de passe de Quai Mail.</summary>
    public static int Run()
    {
        try
        {
            var vault = new PasswordVault();
            PasswordVaultCredentialStore.RemoveAll(vault, PasswordVaultCredentialStore.DefaultResource);
            PasswordVaultCredentialStore.RemoveAll(vault, PasswordVaultCredentialStore.SmokeResource);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
