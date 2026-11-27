using Microsoft.Extensions.Logging;
using QuaiMail.Core.Agent;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Updates;

namespace QuaiMail.App.Services;

/// <summary>
/// L'agent de notification (<c>QuaiMailAgent.exe</c>, à côté de Quai Mail) vu de Quai Mail : pause pendant que la
/// fenêtre est ouverte (<see cref="AppPresence"/>), lancement à sa fermeture si le réglage le permet, lancement avec
/// Windows activé d'office la première fois (copie installée seulement, jamais une copie portable), puis seulement au
/// choix de l'utilisateur. Une mise à jour arrête et relance l'agent elle-même (installer/QuaiMail.iss).
/// </summary>
internal sealed class BackgroundAgent : IBackgroundAgent
{
    private readonly ISettingsService _settings;
    private readonly IUpdateInstaller _installation;
    private readonly ILogger _logger;
    private readonly AutoStart _autoStart = new();
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, AgentInfo.ExeName);
    private AppPresence? _presence;

    public BackgroundAgent(ISettingsService settings, IUpdateInstaller installation, ILogger<BackgroundAgent> logger)
    {
        _settings = settings;
        _installation = installation;
        _logger = logger;
    }

    public bool IsAvailable => File.Exists(_path);

    public bool IsAutoStartEnabled
    {
        get
        {
            try
            {
                return IsAvailable && _autoStart.IsEnabled;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Fermeture pour une mise à jour : l'installateur s'occupe de l'agent, Quai Mail ne le lance pas.</summary>
    public bool ClosingForUpdate { get; set; }

    public void SetAutoStart(bool enabled)
    {
        try
        {
            if (enabled && IsAvailable)
            {
                _autoStart.Enable(_path);
            }
            else if (!enabled)
            {
                _autoStart.Disable();
            }

            _logger.LogInformation("Lancement de l'agent avec Windows {State}", enabled ? "activé" : "désactivé");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning("Lancement de l'agent avec Windows non modifié ({Error})", ex.GetType().Name);
        }
    }

    public void Stop()
    {
        if (AgentControl.Stop(TimeSpan.FromSeconds(5)))
        {
            _logger.LogInformation("Agent de notification arrêté (réglage)");
        }
    }

    /// <summary>Ouverture de la fenêtre (fil de l'interface) : l'agent, s'il tourne, coupe ses connexions.</summary>
    public void OnAppStarted()
    {
        try
        {
            _presence = AppPresence.Acquire();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent non prévenu de l'ouverture de Quai Mail ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Fermeture de la fenêtre (fil de l'interface) : l'agent prend le relais.</summary>
    public void OnAppClosed()
    {
        try
        {
            var settings = _settings.Current;
            if (!ClosingForUpdate && settings.BackgroundAgent && settings.ShowNotifications && IsAvailable)
            {
                if (!_autoStart.WasChosen && _installation.CanInstall)
                {
                    _autoStart.Enable(_path);
                    _logger.LogInformation("Lancement de l'agent avec Windows activé (première fois)");
                }

                if (!AgentControl.IsRunning)
                {
                    AgentControl.Start(_path);
                    _logger.LogInformation("Agent de notification lancé");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Agent de notification non lancé ({Error})", ex.GetType().Name);
        }
        finally
        {
            // Verrou relâché : l'agent (déjà ouvert ou lancé à l'instant) reprend la surveillance.
            _presence?.Dispose();
            _presence = null;
        }
    }
}

/// <summary>Tests de fumée : réglages de l'agent affichés, mais ni registre ni processus touchés.</summary>
internal sealed class SmokeBackgroundAgent : IBackgroundAgent
{
    public bool IsAvailable => true;

    public bool IsAutoStartEnabled { get; private set; }

    public int Stops { get; private set; }

    public void SetAutoStart(bool enabled) => IsAutoStartEnabled = enabled;

    public void Stop() => Stops++;
}
