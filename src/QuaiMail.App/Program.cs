using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace QuaiMail.App;

/// <summary>
/// Point d'entrée (à la place de celui généré par WinUI) : une seule fenêtre de Quai Mail. Un deuxième lancement, par
/// exemple un lien <c>mailto:</c> cliqué alors que Quai Mail est ouvert, passe la main à l'instance déjà ouverte puis
/// s'arrête (modèle « application à instance unique » du Windows App SDK). Les commutateurs techniques (tests,
/// désinstallation) ne sont jamais redirigés.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "QuaiMail";

    private static readonly string[] Standalone = ["--smoke-test", "--update-test", "--purge-secrets", "--unregister-notifications"];

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Lancement passé à l'instance déjà ouverte (arguments de la ligne de commande compris).</summary>
    public static event EventHandler<string>? Redirected;

    private static bool RedirectToRunningInstance()
    {
        if (Environment.GetCommandLineArgs().Any(a => Standalone.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (main.IsCurrent)
            {
                main.Activated += OnActivated;
                return false;
            }

            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = CreateEvent(IntPtr.Zero, true, false, null);
            _ = Task.Run(() =>
            {
                try
                {
                    main.RedirectActivationToAsync(activation).AsTask().Wait();
                }
                finally
                {
                    SetEvent(done);
                }
            });
            _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
            CloseHandle(done);

            // La fenêtre déjà ouverte revient au premier plan.
            using var process = Process.GetProcessById((int)main.ProcessId);
            _ = SetForegroundWindow(process.MainWindowHandle);
            return true;
        }
        catch (Exception)
        {
            // Mécanisme indisponible : Quai Mail s'ouvre normalement.
            return false;
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            Redirected?.Invoke(null, launch.Arguments ?? string.Empty);
        }
        else
        {
            Redirected?.Invoke(null, string.Empty);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("ole32.dll")]
    private static partial uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, [In] IntPtr[] handles, out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
