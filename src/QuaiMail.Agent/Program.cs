using Microsoft.Extensions.Logging;
using QuaiMail.Core;
using QuaiMail.Core.Agent;
using QuaiMail.Core.Storage;
using Serilog;
using Serilog.Extensions.Logging;

namespace QuaiMail.Agent;

/// <summary>
/// <c>QuaiMailAgent.exe</c> : une seule instance par session. <c>--quit</c> arrête l'instance ouverte (installateur,
/// désinstallateur) ; code 0 si elle tournait et s'est arrêtée, 1 sinon.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, AgentInfo.QuitSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            return AgentControl.Stop(TimeSpan.FromSeconds(15)) ? 0 : 1;
        }

        using var instance = new Mutex(true, AgentInfo.InstanceMutexName, out var created);
        if (!created)
        {
            return 0;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();

        // Journal local (pas de télémétrie), à part de celui de Quai Mail. Ni mot de passe, ni objet, ni adresse.
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();
        using var loggers = new SerilogLoggerFactory(serilog);
        var logger = loggers.CreateLogger("QuaiMail.Agent");
        logger.LogInformation("Démarrage de l'agent de Quai Mail {Version}", AppInfo.Version);

        try
        {
            using var agent = new MailAgent(paths, loggers);
            return agent.Run();
        }
        catch (Exception ex)
        {
            logger.LogError("Agent arrêté sur une erreur ({Error})", ex.GetType().Name);
            return 2;
        }
        finally
        {
            logger.LogInformation("Agent arrêté");
        }
    }
}
