using System.Diagnostics;
using Microsoft.Extensions.Logging;
using QuaiMail.Core.Services;

namespace QuaiMail.App.Services;

/// <summary>Navigateur ou messagerie par défaut de Windows, pour les seules adresses http, https et mailto.</summary>
internal sealed class Launcher(ILogger<Launcher> logger) : ILauncher
{
    public bool Open(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Ouverture du lien impossible ({Error})", ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>Test de fumée : rien n'est jamais ouvert, les demandes sont notées.</summary>
internal sealed class SmokeLauncher : ILauncher
{
    public List<Uri> Opened { get; } = [];

    public bool Open(Uri uri)
    {
        Opened.Add(uri);
        return true;
    }
}
