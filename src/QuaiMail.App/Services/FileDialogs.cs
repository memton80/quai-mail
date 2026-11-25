using Microsoft.Windows.Storage.Pickers;
using QuaiMail.App.Localization;
using QuaiMail.Core.Services;

namespace QuaiMail.App.Services;

/// <summary>Sélecteurs de fichiers de Windows (API du Windows App SDK, utilisable sans paquet MSIX ; repris de Quai PDF).</summary>
internal sealed class FileDialogs : IFileDialogs
{
    /// <summary>Identifiant de la fenêtre principale ; renseigné par celle-ci à sa création.</summary>
    public Microsoft.UI.WindowId WindowId { get; set; }

    /// <summary>Test de fumée : dossier où « choisir » l'emplacement sans ouvrir le sélecteur.</summary>
    public string? SmokeDirectory { get; set; }

    /// <summary>Test de fumée : fichiers « choisis » à joindre, sans ouvrir le sélecteur.</summary>
    public List<string> SmokeAttachments { get; } = [];

    public async Task<IReadOnlyList<string>> PickFilesToAttachAsync()
    {
        if (SmokeDirectory is not null)
        {
            return [.. SmokeAttachments];
        }

        var picker = new FileOpenPicker(WindowId) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        var results = await picker.PickMultipleFilesAsync();
        return results is null ? [] : [.. results.Select(r => r.Path)];
    }

    public async Task<string?> PickSaveLocationAsync(string suggestedFileName)
    {
        if (SmokeDirectory is { } smoke)
        {
            return Path.Combine(smoke, suggestedFileName);
        }

        var extension = Path.GetExtension(suggestedFileName);
        var picker = new FileSavePicker(WindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
        };
        if (!string.IsNullOrEmpty(extension))
        {
            picker.FileTypeChoices.Add(Loc.Format("Picker_FileType", extension), [extension]);
        }

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(extension) || string.Equals(Path.GetExtension(result.Path), extension, StringComparison.OrdinalIgnoreCase)
            ? result.Path
            : result.Path + extension;
    }
}
