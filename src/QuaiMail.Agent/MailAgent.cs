using System.Diagnostics;
using Microsoft.Extensions.Logging;
using QuaiMail.App.Services;
using QuaiMail.Core.Accounts;
using QuaiMail.Core.Agent;
using QuaiMail.Core.Mail;
using QuaiMail.Core.Storage;
using static QuaiMail.Agent.NativeMethods;

namespace QuaiMail.Agent;

/// <summary>
/// L'agent : tant que Quai Mail est fermé, la boîte de réception de chaque compte est surveillée en direct (IDLE, la
/// réception en direct de Quai Mail lui-même) et chaque nouveau mail non lu est annoncé par une notification. Dès que
/// Quai Mail s'ouvre, l'agent coupe ses connexions et attend sa fermeture (verrou <see cref="AgentInfo.AppRunningMutexName"/>) :
/// jamais deux connexions ni deux notifications pour le même mail. Après chaque activité, la mémoire inutile est rendue
/// à Windows ; entre deux mails, aucun fil ne tourne (attente sur les connexions et les objets du noyau).
/// </summary>
internal sealed class MailAgent : IDisposable
{
    private static readonly TimeSpan TrimAfterStart = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TrimAfterMail = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TrimPeriod = TimeSpan.FromMinutes(15);

    private readonly AppPaths _paths;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly TrayIcon _tray = new();
    private readonly EventWaitHandle _quit = new(false, EventResetMode.AutoReset, AgentInfo.QuitEventName);
    private readonly EventWaitHandle _appStarted = new(false, EventResetMode.AutoReset, AgentInfo.AppStartedEventName);
    private readonly AutoStart _autoStart = new();
    private readonly PasswordVaultCredentialStore _credentials = new();
    private readonly Timer _trim;
    private readonly Lock _gate = new();
    private MailKitMailWatcher? _watcher;
    private volatile bool _stopping;
    private volatile string? _lastAccountId;

    public MailAgent(AppPaths paths, ILoggerFactory loggers)
    {
        _paths = paths;
        _loggers = loggers;
        _logger = loggers.CreateLogger("QuaiMail.Agent");
        _trim = new Timer(_ => Trim(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Run()
    {
        // Rien à surveiller (agent coupé, notifications coupées, aucun compte) : pas d'icône, l'agent s'arrête aussitôt.
        // Quai Mail ouvert : l'agent attend sa fermeture, les réglages seront relus à ce moment-là.
        if (!IsAppRunning() && !CanWatch(out _))
        {
            return 0;
        }

        _tray.IsAutoStartEnabled = () => _autoStart.IsEnabled;
        _tray.OpenRequested += () => OpenQuaiMail(null);
        _tray.BalloonClicked += () => OpenQuaiMail(_lastAccountId);
        _tray.AutoStartToggled += ToggleAutoStart;
        _tray.Resumed += OnResumed;
        _tray.Create();

        var coordinator = new Thread(Coordinate, 256 * 1024) { IsBackground = true, Name = "Agent" };
        coordinator.Start();
        _tray.Run();

        // Fenêtre fermée (menu « Arrêter l'agent », --quit, fin de session, installateur).
        _stopping = true;
        _quit.Set();
        coordinator.Join(TimeSpan.FromSeconds(15));
        StopWatching();
        return 0;
    }

    public void Dispose()
    {
        _stopping = true;
        _trim.Dispose();
        StopWatching();
        _tray.Dispose();
        _quit.Dispose();
        _appStarted.Dispose();
    }

    /// <summary>Fil de coordination : surveiller, se mettre en pause pendant que Quai Mail est ouvert, reprendre, s'arrêter.</summary>
    private void Coordinate()
    {
        try
        {
            while (!_stopping)
            {
                if (OpenAppMutex() is { } app)
                {
                    StopWatching();
                    _tray.SetStatus(AgentText.TipPaused, AgentText.StatusPaused);
                    _logger.LogInformation("En pause : Quai Mail est ouvert");
                    ScheduleTrim(TimeSpan.Zero);
                    if (!WaitForAppToClose(app))
                    {
                        break;
                    }

                    _logger.LogInformation("Quai Mail fermé : reprise de la surveillance");
                    continue;
                }

                if (!StartWatching())
                {
                    break;
                }

                // Quai Mail s'ouvre (il signale son arrivée) ou arrêt demandé.
                if (WaitHandle.WaitAny([_quit, _appStarted]) == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Coordination de l'agent interrompue ({Error})", ex.GetType().Name);
        }

        if (!_stopping)
        {
            _tray.Close();
        }
    }

    private bool WaitForAppToClose(Mutex app)
    {
        using (app)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny([_quit, app]);
            }
            catch (AbandonedMutexException ex)
            {
                // Quai Mail s'est arrêté brutalement : le verrou est à nous quand même.
                index = ex.MutexIndex == 0 ? 0 : 1;
            }

            if (index != 1)
            {
                return false;
            }

            app.ReleaseMutex();
            return !_stopping;
        }
    }

    private static Mutex? OpenAppMutex() => Mutex.TryOpenExisting(AgentInfo.AppRunningMutexName, out var mutex) ? mutex : null;

    private static bool IsAppRunning()
    {
        using var mutex = OpenAppMutex();
        return mutex is not null;
    }

    /// <summary>Réglages et comptes relus sur le disque (Quai Mail a pu les changer pendant la pause).</summary>
    private bool CanWatch(out IReadOnlyList<Account> accounts)
    {
        accounts = [];
        var settings = new JsonSettingsService(_paths).Current;
        if (!settings.BackgroundAgent || !settings.ShowNotifications)
        {
            _logger.LogInformation("Agent ou notifications coupés dans les réglages de Quai Mail : arrêt");
            return false;
        }

        // Comptes à mot de passe : la connexion par le navigateur (Gmail, Outlook.com) n'est pas encore proposée.
        accounts = [.. new AccountStore(_paths).All.Where(a => a.Auth == AccountAuth.Password)];
        if (accounts.Count == 0)
        {
            _logger.LogInformation("Aucun compte à surveiller : arrêt");
            return false;
        }

        return true;
    }

    private bool StartWatching()
    {
        if (!CanWatch(out var accounts))
        {
            return false;
        }

        lock (_gate)
        {
            if (_watcher is null)
            {
                _watcher = new MailKitMailWatcher(_credentials, _loggers.CreateLogger<MailKitMailWatcher>());
                _watcher.NewMail += OnNewMail;
            }

            _watcher.Watch(accounts);
        }

        _tray.SetStatus(AgentText.TipWatching(accounts.Count), AgentText.StatusWatching(accounts.Count));
        _logger.LogInformation("Surveillance de {Count} compte(s)", accounts.Count);
        ScheduleTrim(TrimAfterStart);
        return true;
    }

    private void StopWatching()
    {
        MailKitMailWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        watcher.NewMail -= OnNewMail;
        try
        {
            watcher.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Connexions fermées au mieux ({Error})", ex.GetType().Name);
        }

        _logger.LogInformation("Surveillance arrêtée");
    }

    /// <summary>Depuis un fil de fond de la réception en direct. Rien du mail n'est journalisé, seulement le nombre.</summary>
    private void OnNewMail(object? sender, NewMailEventArgs e)
    {
        if (_stopping || !ReferenceEquals(sender, Volatile.Read(ref _watcher)))
        {
            return;
        }

        if (NewMailSummary.For(e.Account.Title, e.Messages, AgentText.NoSubject) is { } summary)
        {
            _lastAccountId = e.Account.Id;
            _tray.ShowBalloon(summary.Title, summary.Text);
            _logger.LogInformation("Notification affichée : {Count} nouveau(x) mail(s) non lu(s)", e.Messages.Count(m => !m.IsRead));
        }

        ScheduleTrim(TrimAfterMail);
    }

    private void OnResumed()
    {
        // Connexions sans doute mortes pendant la veille : vérification tout de suite, sans perdre ce qui est arrivé.
        _logger.LogInformation("Sortie de veille : vérification des boîtes de réception");
        Volatile.Read(ref _watcher)?.Wake();
        ScheduleTrim(TrimAfterStart);
    }

    private void ToggleAutoStart()
    {
        try
        {
            if (_autoStart.IsEnabled)
            {
                _autoStart.Disable();
            }
            else if (Environment.ProcessPath is { } exe)
            {
                _autoStart.Enable(exe);
            }

            _logger.LogInformation("Lancement avec Windows {State} (menu de l'icône)", _autoStart.IsEnabled ? "activé" : "désactivé");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lancement avec Windows non modifié ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Ouvre Quai Mail (sur la boîte de réception du compte de la dernière notification, après un clic dessus).</summary>
    private void OpenQuaiMail(string? accountId)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "QuaiMail.exe");
        if (!File.Exists(exe))
        {
            _logger.LogWarning("QuaiMail.exe introuvable à côté de l'agent");
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            if (AgentLaunch.IsAccountId(accountId))
            {
                start.ArgumentList.Add(AgentLaunch.InboxSwitch);
                start.ArgumentList.Add(accountId!);
            }

            using (Process.Start(start))
            {
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Quai Mail non ouvert ({Error})", ex.GetType().Name);
        }
    }

    private void ScheduleTrim(TimeSpan delay)
    {
        if (!_stopping)
        {
            try
            {
                _trim.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Arrêt en cours.
            }
        }
    }

    /// <summary>
    /// Mémoire rendue à Windows : ramasse-miettes complet qui libère ce qui ne sert plus (mode « agressif »), puis
    /// pages inutilisées retirées de la mémoire du processus. Quelques millisecondes, après une activité ou tous les
    /// quarts d'heure.
    /// </summary>
    private void Trim()
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
        }
        catch (Exception)
        {
            // Au mieux.
        }

        ScheduleTrim(TrimPeriod);
    }
}
