using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace QuaiMail.Core.Pgp;

/// <summary>Pourquoi une clé n'a pas pu être reprise de GnuPG.</summary>
public enum GnuPgError
{
    /// <summary>La clé est protégée : il faut sa phrase de passe (celle demandée par Kleopatra).</summary>
    PassphraseNeeded,

    /// <summary>Phrase de passe refusée par GnuPG.</summary>
    WrongPassphrase,

    /// <summary>GnuPG n'a pas répondu, ou a refusé pour une autre raison.</summary>
    Failed,
}

public sealed class GnuPgException(GnuPgError error, Exception? inner = null) : Exception("GnuPG : " + error, inner)
{
    public GnuPgError Error { get; } = error;
}

/// <summary>Une clé secrète trouvée dans GnuPG (le trousseau de Kleopatra), capable de signer.</summary>
/// <param name="Fingerprint">Empreinte de la clé principale, en majuscules.</param>
/// <param name="UserId">Identité principale (« Nom &lt;adresse&gt; »).</param>
/// <param name="OnCard">La partie secrète est sur une carte à puce : elle ne peut pas être copiée.</param>
public sealed record GnuPgKey(string Fingerprint, string UserId, IReadOnlyList<string> Addresses, DateTimeOffset? Expires, bool OnCard)
{
    /// <summary>Empreinte lisible : groupes de 4, comme Kleopatra.</summary>
    public string DisplayFingerprint => string.Join(' ', Enumerable.Range(0, Fingerprint.Length / 4).Select(i => Fingerprint.Substring(i * 4, 4)));
}

/// <summary>
/// GnuPG installé sur l'ordinateur (celui de Kleopatra / Gpg4win). Quai Mail ne s'en sert que pour lire la liste des
/// clés secrètes et en copier une dans son propre trousseau, sur demande ; il ne le modifie jamais et ne signe pas avec.
/// </summary>
public interface IGnuPg
{
    /// <summary>Kleopatra est installé avec : c'est le nom que l'utilisateur connaît.</summary>
    bool HasKleopatra { get; }

    /// <summary>Clés secrètes capables de signer (ni expirées, ni révoquées), sans phrase de passe.</summary>
    Task<IReadOnlyList<GnuPgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clé secrète exportée (armurée), en mémoire seulement : elle reste protégée par sa phrase de passe, comme dans
    /// GnuPG. Sans phrase de passe (<c>null</c>), GnuPG essaie sans : clé non protégée, ou phrase encore en mémoire.
    /// </summary>
    Task<string> ExportSecretKeyAsync(string fingerprint, string? passphrase, CancellationToken cancellationToken = default);
}

/// <summary>Trouve GnuPG sur l'ordinateur.</summary>
public interface IGnuPgLocator
{
    /// <returns>GnuPG, ou <c>null</c> s'il n'est pas installé.</returns>
    IGnuPg? Find();
}

/// <summary>
/// Cherche <c>gpg.exe</c> là où Gpg4win et GnuPG l'installent (registre, puis Program Files), puis dans le PATH ;
/// Kleopatra à côté. Seulement des chemins complets : jamais le dossier courant.
/// </summary>
public sealed class GnuPgLocator : IGnuPgLocator
{
    public IGnuPg? Find()
    {
        var gpg = Candidates().FirstOrDefault(File.Exists);
        return gpg is null ? null : new GnuPgProcess(gpg, FindKleopatra() is not null);
    }

    /// <summary>Emplacements possibles de gpg, du plus sûr au moins sûr.</summary>
    public static IEnumerable<string> Candidates()
    {
        var name = OperatingSystem.IsWindows() ? "gpg.exe" : "gpg";
        if (OperatingSystem.IsWindows())
        {
            foreach (var directory in InstallDirectories("GnuPG").Concat(ProgramFiles().Select(p => Path.Combine(p, "GnuPG"))))
            {
                yield return Path.Combine(directory, "bin", name);
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length > 0 && Path.IsPathFullyQualified(directory))
            {
                yield return Path.Combine(directory, name);
            }
        }
    }

    private static string? FindKleopatra()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return InstallDirectories("GPG4Win")
            .Concat(ProgramFiles().Select(p => Path.Combine(p, "Gpg4win")))
            .Select(d => Path.Combine(d, "bin", "kleopatra.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> ProgramFiles() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dossier d'installation noté par l'installateur (« Install Directory »), en 64 et en 32 bits.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> InstallDirectories(string product)
    {
        var found = new List<string>();
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\" + product);
                if (key?.GetValue("Install Directory") is string directory && Path.IsPathFullyQualified(directory))
                {
                    found.Add(directory);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // Clé illisible : on passe aux emplacements suivants.
            }
        }

        return found;
    }
}

/// <summary>
/// <c>gpg</c> lancé sans fenêtre, en mode non interactif. La phrase de passe passe par l'entrée standard
/// (<c>--pinentry-mode loopback</c>, permis par défaut depuis GnuPG 2.1.12), jamais par la ligne de commande ; la
/// clé exportée ne passe que par la mémoire. Les réponses sont lues dans les lignes d'état (<c>[GNUPG:]</c>), qui ne
/// dépendent pas de la langue.
/// </summary>
public sealed class GnuPgProcess(string executable, bool hasKleopatra = false, string? homeDirectory = null) : IGnuPg
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public string Executable => executable;

    public bool HasKleopatra => hasKleopatra;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<GnuPgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, output, _) = await RunAsync(["--with-colons", "--fixed-list-mode", "--list-secret-keys"], null, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new GnuPgException(GnuPgError.Failed);
        }

        return ParseSecretKeys(output, DateTimeOffset.UtcNow);
    }

    public async Task<string> ExportSecretKeyAsync(string fingerprint, string? passphrase, CancellationToken cancellationToken = default)
    {
        // Une empreinte, rien d'autre : pas d'option glissée dans la ligne de commande.
        if (fingerprint.Length is not (40 or 64) || !fingerprint.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Empreinte OpenPGP attendue.", nameof(fingerprint));
        }

        var (exitCode, output, errors) = await RunAsync(
            ["--pinentry-mode", "loopback", "--passphrase-fd", "0", "--status-fd", "2", "--armor", "--export-secret-keys", fingerprint],
            passphrase is null ? null : passphrase + "\n",
            cancellationToken).ConfigureAwait(false);
        if (exitCode == 0 && output.Contains("-----BEGIN PGP PRIVATE KEY BLOCK-----", StringComparison.Ordinal))
        {
            return output;
        }

        throw new GnuPgException(ExportError(errors));
    }

    /// <summary>
    /// Erreur d'export d'après les lignes d'état : « ERROR export_keys.secret &lt;code&gt; », dont les 16 bits de poids
    /// faible sont le code libgpg-error (11 : mauvaise phrase de passe ; 177 : pas de phrase de passe ; 99 : annulé).
    /// </summary>
    public static GnuPgError ExportError(string status)
    {
        foreach (var line in status.Split('\n'))
        {
            var parts = line.Trim().Split(' ');
            if (parts is ["[GNUPG:]", "ERROR", _, var value, ..] && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                switch (code & 0xFFFF)
                {
                    case 11:
                        return GnuPgError.WrongPassphrase;
                    case 177 or 99:
                        return GnuPgError.PassphraseNeeded;
                }
            }
            else if (parts is ["[GNUPG:]", "BAD_PASSPHRASE", ..])
            {
                return GnuPgError.WrongPassphrase;
            }
        }

        return GnuPgError.Failed;
    }

    /// <summary>
    /// Lit <c>gpg --with-colons --fixed-list-mode --list-secret-keys</c> : les clés qui peuvent signer (capacité « S »
    /// de la clé entière), ni révoquées, ni expirées, ni désactivées, dont la partie secrète est là (« + ») ou sur une
    /// carte à puce (numéro de série) ; les simples talons (« # ») sont écartés.
    /// </summary>
    public static IReadOnlyList<GnuPgKey> ParseSecretKeys(string colons, DateTimeOffset now)
    {
        var keys = new List<GnuPgKey>();
        Pending? current = null;
        foreach (var raw in colons.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split(':');
            switch (fields[0])
            {
                case "sec":
                    Add(current);
                    current = new Pending(fields);
                    break;
                case "fpr" when current is { Fingerprint: null } && fields.Length > 9:
                    current.Fingerprint = fields[9].ToUpperInvariant();
                    break;
                case "uid" when current is not null && fields.Length > 9 && Field(fields, 1) is not ("r" or "e" or "i"):
                    current.UserIds.Add(Unescape(fields[9]));
                    break;
                case "ssb" when current is not null:
                    current.Subkeys.Add(fields);
                    break;
            }
        }

        Add(current);
        return keys;

        void Add(Pending? pending)
        {
            if (pending?.ToKey(now) is { } key)
            {
                keys.Add(key);
            }
        }
    }

    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index] : string.Empty;

    /// <summary>Champs d'identité : « \xNN » pour les deux-points et les caractères de contrôle, texte en UTF-8.</summary>
    private static string Unescape(string value)
    {
        var source = Encoding.UTF8.GetBytes(value);
        var bytes = new List<byte>(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\\' && i + 3 < source.Length && source[i + 1] == (byte)'x'
                && byte.TryParse(Encoding.ASCII.GetString(source, i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 3;
            }
            else
            {
                bytes.Add(source[i]);
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static IReadOnlyList<string> AddressesOf(IEnumerable<string> userIds) =>
        [.. userIds
            .Select(u => u.LastIndexOf('<') is var start and >= 0 && u.IndexOf('>', start) is var end and > 0 ? u[(start + 1)..end] : u)
            .Where(a => a.Contains('@', StringComparison.Ordinal))
            .Select(a => a.Trim().ToLowerInvariant())
            .Distinct()];

    private async Task<(int ExitCode, string Output, string Errors)> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (homeDirectory is not null)
        {
            start.ArgumentList.Add("--homedir");
            start.ArgumentList.Add(homeDirectory);
        }

        start.ArgumentList.Add("--batch");
        start.ArgumentList.Add("--no-tty");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new GnuPgException(GnuPgError.Failed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new GnuPgException(GnuPgError.Failed, ex);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                if (input is not null)
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // gpg n'a pas lu la phrase de passe (clé non protégée) : rien à faire.
            }

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return (process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Déjà terminé.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new GnuPgException(GnuPgError.Failed);
            }
        }
    }

    /// <summary>Clé en cours de lecture (ligne « sec » et les suivantes).</summary>
    private sealed class Pending(string[] sec)
    {
        public string? Fingerprint { get; set; }

        public List<string> UserIds { get; } = [];

        public List<string[]> Subkeys { get; } = [];

        public GnuPgKey? ToKey(DateTimeOffset now)
        {
            DateTimeOffset? expires = long.TryParse(Field(sec, 6), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
            if (Fingerprint is null || Field(sec, 1) is "r" or "e" or "i" or "d" || !Field(sec, 11).Contains('S', StringComparison.Ordinal)
                || expires <= now)
            {
                return null;
            }

            // Parties qui signent elles-mêmes (« s » minuscule) et encore valables : la clé principale, ses sous-clés.
            var signers = Subkeys
                .Where(s => Field(s, 1) is not ("r" or "e" or "i"))
                .Prepend(sec)
                .Where(k => Field(k, 11).Contains('s', StringComparison.Ordinal))
                .Select(k => Field(k, 14))
                .ToList();
            var present = signers.Contains("+");
            var onCard = !present && signers.Any(token => token.Length > 0 && token != "#");
            if (!present && !onCard)
            {
                return null;
            }

            var userId = UserIds.FirstOrDefault() ?? Fingerprint;
            return new GnuPgKey(Fingerprint, userId, AddressesOf(UserIds), expires, onCard);
        }
    }
}
