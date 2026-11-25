using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.Drafts;

/// <summary>
/// Brouillons enregistrés sur cet ordinateur (<c>%LOCALAPPDATA%\QuaiMail\drafts</c>), un fichier JSON par brouillon,
/// écrit d'un bloc (<see cref="AtomicFile"/>) : jamais de brouillon à moitié écrit. Les journaux ne contiennent que
/// l'identifiant, jamais l'objet, le corps ni les destinataires.
/// </summary>
public sealed class DraftStore
{
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DraftStore(AppPaths paths, ILogger<DraftStore>? logger = null)
        : this(paths.DraftsDirectory, TimeProvider.System, logger)
    {
    }

    public DraftStore(string directory, TimeProvider? time = null, ILogger<DraftStore>? logger = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DraftStore>.Instance;
    }

    public string Directory => _directory;

    /// <summary>Enregistre (ou remplace) le brouillon ; une erreur d'écriture remonte à l'appelant (rien n'est perdu en silence).</summary>
    public void Save(Draft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Draft.IsValidId(draft.Id))
        {
            throw new ArgumentException("Identifiant de brouillon invalide.", nameof(draft));
        }

        draft.UpdatedAt = _time.GetUtcNow();
        System.IO.Directory.CreateDirectory(_directory);
        AtomicFile.Write(PathOf(draft.Id), JsonSerializer.SerializeToUtf8Bytes(draft, QuaiMailJsonContext.Default.Draft));
    }

    /// <returns>Le brouillon, ou <c>null</c> s'il n'existe pas ou est illisible.</returns>
    public Draft? Load(string id)
    {
        if (!Draft.IsValidId(id))
        {
            return null;
        }

        var path = PathOf(id);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var draft = JsonSerializer.Deserialize(File.ReadAllBytes(path), QuaiMailJsonContext.Default.Draft);
            if (draft is null || draft.Id != id)
            {
                _logger.LogWarning("Brouillon {Id} illisible", id);
                return null;
            }

            return draft;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("Brouillon {Id} illisible ({Error})", id, ex.GetType().Name);
            return null;
        }
    }

    /// <returns><c>true</c> si un fichier a été supprimé (pièces jointes copiées comprises).</returns>
    public bool Delete(string id)
    {
        if (!Draft.IsValidId(id))
        {
            return false;
        }

        var files = FilesOf(id);
        if (System.IO.Directory.Exists(files))
        {
            System.IO.Directory.Delete(files, recursive: true);
        }

        var path = PathOf(id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Joint un fichier au brouillon : son contenu est copié d'un bloc dans le dossier du brouillon (le brouillon ne
    /// dépend plus du fichier d'origine, qui peut être déplacé ou effacé).
    /// </summary>
    public async Task<DraftAttachment> AddAttachmentAsync(Draft draft, string fileName, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Draft.IsValidId(draft.Id))
        {
            throw new ArgumentException("Identifiant de brouillon invalide.", nameof(draft));
        }

        var files = FilesOf(draft.Id);
        System.IO.Directory.CreateDirectory(files);
        var stored = Guid.NewGuid().ToString("N");
        var path = Path.Combine(files, stored);
        await AtomicFile.WriteAsync(path, write, cancellationToken).ConfigureAwait(false);
        var attachment = new DraftAttachment(Reading.AttachmentRules.SafeFileName(fileName), stored, new FileInfo(path).Length);
        draft.Attachments.Add(attachment);
        return attachment;
    }

    /// <summary>Retire une pièce jointe du brouillon (et sa copie).</summary>
    public void RemoveAttachment(Draft draft, DraftAttachment attachment)
    {
        draft.Attachments.Remove(attachment);
        var path = AttachmentPath(draft, attachment);
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Fichier copié d'une pièce jointe ; <c>null</c> si son nom n'est pas l'un des nôtres.</summary>
    public string? AttachmentPath(Draft draft, DraftAttachment attachment) =>
        Draft.IsValidId(draft.Id) && Draft.IsValidId(attachment.StoredName)
            ? Path.Combine(FilesOf(draft.Id), attachment.StoredName)
            : null;

    /// <summary>Brouillons gardés, du plus récent au plus ancien (les fichiers illisibles sont ignorés).</summary>
    public IReadOnlyList<Draft> List()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        return [.. System.IO.Directory.EnumerateFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(id => Load(id!))
            .OfType<Draft>()
            .OrderByDescending(d => d.UpdatedAt)];
    }

    private string PathOf(string id) => Path.Combine(_directory, id + ".json");

    private string FilesOf(string id) => Path.Combine(_directory, id + ".files");
}
