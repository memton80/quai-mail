namespace QuaiMail.Core.Services;

/// <summary>Sélecteurs de fichiers de Windows (implémentés par l'application).</summary>
public interface IFileDialogs
{
    /// <summary>Emplacement où enregistrer un fichier ; <c>null</c> si l'utilisateur annule.</summary>
    Task<string?> PickSaveLocationAsync(string suggestedFileName);

    /// <summary>Fichiers à joindre à un message ; liste vide si l'utilisateur annule.</summary>
    Task<IReadOnlyList<string>> PickFilesToAttachAsync();
}
