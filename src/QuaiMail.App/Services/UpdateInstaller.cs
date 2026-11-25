using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Updates;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.App.Services;

/// <summary>
/// Installation d'une mise à jour téléchargée (repris de Quai et Quai PDF) : l'installateur Inno Setup de la release
/// est lancé en mode silencieux (petite fenêtre de progression, aucune question), Quai Mail se ferme, et l'installateur
/// le relance une fois à jour (<c>/RELAUNCH=1</c>, voir <c>installer/QuaiMail.iss</c>). Installation par utilisateur :
/// aucune élévation. Un brouillon en cours est d'abord proposé à « Garder » ; l'état de la fenêtre (compte, dossier,
/// mail ouvert, brouillon) est noté pour être restauré par la nouvelle version.
/// </summary>
internal sealed class UpdateInstaller : IUpdateInstaller
{
    // Entrée de désinstallation écrite par Inno Setup (AppId de installer/QuaiMail.iss, à ne jamais changer).
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{45AC4609-96F5-4C2C-A740-E243A390EA78}_is1";

    private readonly ShellViewModel _shell;
    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private bool? _canInstall;

    public UpdateInstaller(ShellViewModel shell, AppPaths paths, ILogger<UpdateInstaller> logger)
    {
        _shell = shell;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Quai Mail tourne depuis le dossier où l'installateur l'a mis (sinon : copie portable ou développement).</summary>
    public bool CanInstall => _canInstall ??= IsRunningFromInstallation();

    public async Task<bool> InstallAndRestartAsync(string installerPath)
    {
        // Brouillon en cours : garder, supprimer, ou annuler (la mise à jour attend, déjà téléchargée). Puis l'état de
        // la fenêtre est noté dans updates/reopen.txt.
        if (!await _shell.PrepareForUpdateAsync())
        {
            return false;
        }

        var start = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
        };

        // /SILENT : seulement la fenêtre de progression ; /CLOSEAPPLICATIONS : ferme Quai Mail s'il n'a pas encore fini
        // de quitter ; /RELAUNCH=1 : relance Quai Mail une fois installé ; journal à côté de ceux de Quai Mail.
        foreach (var argument in new[]
        {
            "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1",
            $"/LOG={Path.Combine(_paths.LogsDirectory, "update-install.log")}",
        })
        {
            start.ArgumentList.Add(argument);
        }

        using (Process.Start(start) ?? throw new InvalidOperationException("L'installateur ne s'est pas lancé."))
        {
        }

        _logger.LogInformation("Installateur de la mise à jour lancé ({File}) ; fermeture de Quai Mail", Path.GetFileName(installerPath));
        App.Current.MainWindow?.CloseForUpdate();
        return true;
    }

    public void OpenReleasePage(Uri page)
    {
        try
        {
            // Navigateur par défaut ; l'adresse est construite par l'application (page de release GitHub).
            Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ouverture de la page de mise à jour impossible ({Error})", ex.GetType().Name);
        }
    }

    private bool IsRunningFromInstallation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            var same = string.Equals(installed, running, StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("Mise à jour en place {State}", same ? "possible" : "impossible (copie portable)");
            return same;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            _logger.LogInformation("Installation introuvable dans le registre ({Error})", ex.GetType().Name);
            return false;
        }
    }
}
