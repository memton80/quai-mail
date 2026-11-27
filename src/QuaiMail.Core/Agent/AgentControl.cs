using System.Diagnostics;
using System.Runtime.Versioning;

namespace QuaiMail.Core.Agent;

/// <summary>
/// Côté Quai Mail (et installateur, par <c>QuaiMailAgent.exe --quit</c>) : savoir si l'agent tourne, l'arrêter, le
/// lancer. Rien ne passe par le réseau ni par un fichier : des objets nommés du noyau, dans la session de l'utilisateur.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentControl
{
    /// <summary>L'agent tourne dans cette session.</summary>
    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(AgentInfo.InstanceMutexName, out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
    }

    /// <summary>Demande à l'agent de s'arrêter et attend qu'il l'ait fait.</summary>
    /// <returns><c>true</c> si un agent tournait et s'est arrêté à temps.</returns>
    public static bool Stop(TimeSpan timeout)
    {
        if (!EventWaitHandle.TryOpenExisting(AgentInfo.QuitEventName, out var quit))
        {
            return false;
        }

        using (quit)
        {
            quit.Set();
        }

        var watch = Stopwatch.StartNew();
        while (IsRunning)
        {
            if (watch.Elapsed > timeout)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    /// <summary>Lance l'agent (s'il tourne déjà, la nouvelle instance s'arrête aussitôt d'elle-même).</summary>
    public static void Start(string agentPath)
    {
        var start = new ProcessStartInfo(agentPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(agentPath) ?? string.Empty,
        };
        using (Process.Start(start))
        {
        }
    }
}

/// <summary>
/// Tenu par Quai Mail tant que sa fenêtre est ouverte : l'agent, réveillé par <see cref="AgentInfo.AppStartedEventName"/>,
/// coupe ses connexions et attend ce verrou. À la fermeture de Quai Mail (ou s'il plante), le verrou est libéré et l'agent
/// reprend la surveillance. Les deux ne notifient donc jamais en double.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppPresence : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    private AppPresence(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>À appeler depuis le fil de la fenêtre (le verrou appartient au fil qui le prend), une fois l'instance unique établie.</summary>
    public static AppPresence Acquire()
    {
        var mutex = new Mutex(false, AgentInfo.AppRunningMutexName);
        bool owned;
        try
        {
            // L'agent ne le tient que le temps de le relâcher (reprise après une fermeture de Quai Mail).
            owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (EventWaitHandle.TryOpenExisting(AgentInfo.AppStartedEventName, out var started))
        {
            using (started)
            {
                started.Set();
            }
        }

        return new AppPresence(mutex, owned);
    }

    public void Dispose()
    {
        if (_owned)
        {
            _owned = false;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Pas sur le fil propriétaire : la fin du processus le libère.
            }
        }

        _mutex.Dispose();
    }
}
