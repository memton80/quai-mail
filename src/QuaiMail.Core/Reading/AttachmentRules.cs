namespace QuaiMail.Core.Reading;

/// <summary>Pièces jointes : nom de fichier sûr, types exécutables signalés. Jamais ouvertes automatiquement.</summary>
public static class AttachmentRules
{
    /// <summary>Extensions qui exécutent du code si on ouvre le fichier : un avertissement précède l'enregistrement.</summary>
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".jar", ".lnk", ".url", ".reg",
        ".dll", ".sys", ".inf", ".iso", ".img", ".vhd", ".vhdx", ".application", ".gadget", ".scf", ".chm", ".xll",
        ".docm", ".xlsm", ".pptm", ".one", ".settingcontent-ms",
    };

    public static bool IsExecutable(string fileName) =>
        Executable.Contains(Path.GetExtension(SafeFileName(fileName)));

    /// <summary>
    /// Nom de fichier proposé à l'enregistrement : sans dossier, sans caractère interdit par Windows, sans nom réservé
    /// (CON, NUL…), sans double extension trompeuse cachée par des espaces, jamais vide.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var file = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty);
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        var cleaned = new string([.. file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            return "piece-jointe";
        }

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "LPT1" or "LPT2" or "LPT3")
        {
            cleaned = "_" + cleaned;
        }

        return cleaned.Length > 180 ? cleaned[..120] + Path.GetExtension(cleaned) : cleaned;
    }
}
