using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Models;

namespace QuaiMail.Core.Storage;

/// <summary>
/// État de la fenêtre au moment d'une mise à jour (<c>updates/reopen.txt</c>, JSON) : écrit juste avant de lancer
/// l'installateur, lu une seule fois par la nouvelle version (<c>--after-update</c>), puis effacé.
/// </summary>
public sealed class WindowStateStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public WindowStateStore(AppPaths paths, ILogger<WindowStateStore>? logger = null)
        : this(paths.ReopenAfterUpdateFile, logger)
    {
    }

    public WindowStateStore(string path, ILogger<WindowStateStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<WindowStateStore>.Instance;
    }

    public string FilePath => _path;

    /// <summary>Note l'état à restaurer après la mise à jour (remplace un état plus ancien).</summary>
    public void SaveForUpdate(WindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(state, QuaiMailJsonContext.Default.WindowState));
    }

    /// <summary>
    /// Lit l'état noté puis efface le fichier, même illisible : il ne sert qu'une fois.
    /// </summary>
    /// <returns>L'état, ou <c>null</c> s'il n'y en a pas (ou s'il est illisible).</returns>
    public WindowState? TakeAfterUpdate()
    {
        byte[] content;
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            content = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("État de la fenêtre à restaurer illisible ({Error})", ex.GetType().Name);
            Forget();
            return null;
        }

        Forget();
        try
        {
            return JsonSerializer.Deserialize(content, QuaiMailJsonContext.Default.WindowState);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("État de la fenêtre à restaurer illisible ({Error})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Efface l'état noté (s'il existe).</summary>
    public void Forget()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("État de la fenêtre noté pour la mise à jour impossible à effacer ({Error})", ex.GetType().Name);
        }
    }
}
