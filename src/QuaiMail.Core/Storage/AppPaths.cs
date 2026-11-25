namespace QuaiMail.Core.Storage;

/// <summary>
/// Emplacements des fichiers de l'application (par défaut <c>%LOCALAPPDATA%\QuaiMail</c>).
/// La variable d'environnement <c>QUAI_MAIL_DATA_DIR</c> permet de rediriger le tout (tests, CI).
/// </summary>
public sealed class AppPaths
{
    public const string DataDirectoryVariable = "QUAI_MAIL_DATA_DIR";

    public AppPaths(string? root = null)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        Root = !string.IsNullOrWhiteSpace(root)
            ? root
            : !string.IsNullOrWhiteSpace(fromEnvironment)
                ? fromEnvironment
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Comptes configurés (sans mot de passe : ceux-ci sont dans le coffre de Windows).</summary>
    public string AccountsFile => Path.Combine(Root, "accounts.json");

    /// <summary>Expéditeurs sûrs (adresses et domaines), sur cet ordinateur seulement.</summary>
    public string TrustedSendersFile => Path.Combine(Root, "trusted-senders.json");

    /// <summary>Cache local (dossiers, en-têtes) : peut être effacé à tout moment, il se reconstruit depuis les serveurs.</summary>
    public string CacheDirectory => Path.Combine(Root, "cache");

    public string CacheDatabase => Path.Combine(CacheDirectory, "mail.db");

    /// <summary>Données de la WebView2 de lecture : vidées à la fermeture et au démarrage, aucun cookie gardé.</summary>
    public string WebViewDirectory => Path.Combine(Root, "webview");

    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Brouillons enregistrés sur cet ordinateur (un fichier par brouillon).</summary>
    public string DraftsDirectory => Path.Combine(Root, "drafts");

    /// <summary>Trousseau OpenPGP de Quai Mail (clés secrètes importées, protégées par leur phrase de passe).</summary>
    public string PgpDirectory => Path.Combine(Root, "pgp");

    /// <summary>Clés publiques reçues du serveur de clés, une par fichier (vérifier les mails signés sans les redemander).</summary>
    public string PgpKeyServerCache => Path.Combine(PgpDirectory, "keyserver");

    /// <summary>Installateur de la mise à jour en cours (effacé au démarrage suivant).</summary>
    public string UpdatesDirectory => Path.Combine(Root, "updates");

    /// <summary>État de la fenêtre au moment d'une mise à jour (compte, dossier, mail ouvert, brouillon) : restauré une fois par la nouvelle version, puis effacé.</summary>
    public string ReopenAfterUpdateFile => Path.Combine(UpdatesDirectory, "reopen.txt");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
