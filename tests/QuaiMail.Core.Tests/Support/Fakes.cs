using QuaiMail.Core.Localization;
using QuaiMail.Core.Updates;

namespace QuaiMail.Core.Tests.Support;

/// <summary>Chaînes de test : la clé, suivie des arguments entre parenthèses.</summary>
public sealed class KeyStrings : IStrings
{
    public string Get(string key) => key;

    public string Format(string key, params object?[] args) => $"{key}({string.Join(",", args)})";
}

/// <summary>Installation scriptée d'une mise à jour : enregistre ce qui serait lancé (repris de Quai).</summary>
public sealed class FakeUpdateInstaller : IUpdateInstaller
{
    public bool CanInstall { get; set; } = true;

    /// <summary>Réponse à la question (brouillon en cours) : faux = l'utilisateur garde son brouillon ouvert.</summary>
    public bool Accept { get; set; } = true;

    public Exception? Failure { get; set; }

    public List<string> Installed { get; } = [];

    public List<Uri> OpenedPages { get; } = [];

    public Task<bool> InstallAndRestartAsync(string installerPath)
    {
        if (Failure is not null)
        {
            return Task.FromException<bool>(Failure);
        }

        if (Accept)
        {
            Installed.Add(installerPath);
        }

        return Task.FromResult(Accept);
    }

    public void OpenReleasePage(Uri page) => OpenedPages.Add(page);
}

/// <summary>Boîtes de dialogue scriptées : réponses données à l'avance, questions posées enregistrées.</summary>
public sealed class FakeDialogService : QuaiMail.Core.Services.IDialogService
{
    public Queue<QuaiMail.Core.Services.DraftChoice> DraftAnswers { get; } = new();

    public List<string> AskedDrafts { get; } = [];

    public List<string> Errors { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public List<(string Title, string Message, bool Destructive)> Confirmations { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        Confirmations.Add((title, message, isDestructive));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add(title);
        return Task.CompletedTask;
    }

    public Task<QuaiMail.Core.Services.DraftChoice> AskKeepDraftAsync(string subject)
    {
        AskedDrafts.Add(subject);
        return Task.FromResult(DraftAnswers.Count > 0 ? DraftAnswers.Dequeue() : QuaiMail.Core.Services.DraftChoice.Cancel);
    }

    /// <summary>Réponses successives à la phrase de passe (<c>null</c> = Annuler).</summary>
    public Queue<string?> Passphrases { get; } = new();

    public List<(string Key, bool Retry)> AskedPassphrases { get; } = [];

    public Task<string?> AskPassphraseAsync(string key, bool retry)
    {
        AskedPassphrases.Add((key, retry));
        return Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);
    }
}

/// <summary>Coffre en mémoire (le vrai est le coffre de Windows, dans l'application).</summary>
public sealed class MemoryCredentialStore : QuaiMail.Core.Accounts.ICredentialStore
{
    public Dictionary<string, string> Passwords { get; } = [];

    public string? GetPassword(string accountId) => Passwords.GetValueOrDefault(accountId);

    public void SetPassword(string accountId, string password) => Passwords[accountId] = password;

    public void Remove(string accountId) => Passwords.Remove(accountId);
}

/// <summary>Service de mail scripté pour les view-models (dossiers et pages en mémoire, erreurs à la demande).</summary>
public sealed class FakeMailService : QuaiMail.Core.Mail.IMailService
{
    public Dictionary<string, List<QuaiMail.Core.Mail.MailFolderInfo>> Folders { get; } = [];

    /// <summary>Mails par dossier, du plus ancien au plus récent.</summary>
    public Dictionary<string, List<QuaiMail.Core.Mail.MessageHeader>> Messages { get; } = [];

    public QuaiMail.Core.Mail.MailConnectionException? Failure { get; set; }

    public List<string> Calls { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public Task VerifyAsync(QuaiMail.Core.Accounts.Account account, string password, CancellationToken cancellationToken = default)
    {
        Calls.Add("verify");
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    public Task<IReadOnlyList<QuaiMail.Core.Mail.MailFolderInfo>> GetFoldersAsync(QuaiMail.Core.Accounts.Account account, CancellationToken cancellationToken = default)
    {
        Calls.Add("folders " + account.Id);
        return Failure is null
            ? Task.FromResult<IReadOnlyList<QuaiMail.Core.Mail.MailFolderInfo>>(Folders.GetValueOrDefault(account.Id) ?? [])
            : Task.FromException<IReadOnlyList<QuaiMail.Core.Mail.MailFolderInfo>>(Failure);
    }

    public async Task<QuaiMail.Core.Mail.MessagePage> GetMessagesAsync(QuaiMail.Core.Accounts.Account account, string folder, int skip, int take, CancellationToken cancellationToken = default)
    {
        Calls.Add($"messages {folder} {skip}");
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        var all = Messages.GetValueOrDefault(folder) ?? [];
        var page = all.AsEnumerable().Reverse().Skip(skip).Take(take).ToList();
        return new QuaiMail.Core.Mail.MessagePage(1, all.Count, page);
    }

    /// <summary>Mails complets par UID.</summary>
    public Dictionary<uint, MimeKit.MimeMessage> Contents { get; } = [];

    public List<(uint Uid, bool? Seen, bool? Flagged)> FlagChanges { get; } = [];

    public Task<QuaiMail.Core.Reading.MessageContent> GetMessageAsync(QuaiMail.Core.Accounts.Account account, string folder, uint uid, CancellationToken cancellationToken = default)
    {
        Calls.Add($"message {folder} {uid}");
        if (Failure is not null)
        {
            return Task.FromException<QuaiMail.Core.Reading.MessageContent>(Failure);
        }

        var message = Contents.GetValueOrDefault(uid) ?? new MimeKit.MimeMessage([new MimeKit.MailboxAddress("Bob", "bob@example.org")], [], $"Objet {uid}", new MimeKit.TextPart("plain") { Text = $"Corps {uid}" });
        return Task.FromResult(QuaiMail.Core.Reading.MessageContent.FromMime(uid, message));
    }

    public Task SetFlagsAsync(QuaiMail.Core.Accounts.Account account, string folder, uint uid, bool? seen, bool? flagged, CancellationToken cancellationToken = default)
    {
        FlagChanges.Add((uid, seen, flagged));
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    /// <summary>Commandes groupées reçues (une par dossier pour une sélection) : « opération dossier uid,uid… ».</summary>
    public List<string> Batches { get; } = [];

    /// <summary>Dossier dont le serveur refuse les commandes groupées (connexion perdue), <c>null</c> : aucun.</summary>
    public string? FailingFolder { get; set; }

    public Task SetFlagsAsync(QuaiMail.Core.Accounts.Account account, string folder, IReadOnlyList<uint> uids, bool? seen, bool? flagged, CancellationToken cancellationToken = default)
    {
        if (Batch("flags", folder, uids) is { } failure)
        {
            return Task.FromException(failure);
        }

        FlagChanges.AddRange(uids.Select(uid => (uid, seen, flagged)));
        return Task.CompletedTask;
    }

    private Exception? Batch(string operation, string folder, IReadOnlyList<uint> uids)
    {
        Batches.Add($"{operation} {folder} {string.Join(',', uids)}");
        return Failure ?? (folder == FailingFolder ? new QuaiMail.Core.Mail.MailConnectionException(QuaiMail.Core.Mail.MailError.ConnectionFailed, "imap.example.org:993", "IMAP") : null);
    }

    /// <summary>Mails « envoyés » (gardés en mémoire, jamais acheminés).</summary>
    public List<MimeKit.MimeMessage> Sent { get; } = [];

    public List<QuaiMail.Core.Mail.ServerCopy> SavedDrafts { get; } = [];

    public List<QuaiMail.Core.Mail.ServerCopy> DeletedDrafts { get; } = [];

    public QuaiMail.Core.Mail.MailConnectionException? SendFailure { get; set; }

    private uint _nextDraftUid = 100;

    public Task<QuaiMail.Core.Mail.SendOutcome> SendAsync(QuaiMail.Core.Accounts.Account account, MimeKit.MimeMessage message, CancellationToken cancellationToken = default)
    {
        if (SendFailure is not null)
        {
            return Task.FromException<QuaiMail.Core.Mail.SendOutcome>(SendFailure);
        }

        Sent.Add(message);
        return Task.FromResult(new QuaiMail.Core.Mail.SendOutcome("Envoyés"));
    }

    public Task<QuaiMail.Core.Mail.ServerCopy?> SaveDraftAsync(QuaiMail.Core.Accounts.Account account, MimeKit.MimeMessage message, QuaiMail.Core.Mail.ServerCopy? previous, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException<QuaiMail.Core.Mail.ServerCopy?>(Failure);
        }

        if (previous is not null)
        {
            DeletedDrafts.Add(previous);
        }

        var copy = new QuaiMail.Core.Mail.ServerCopy("Brouillons", _nextDraftUid++);
        SavedDrafts.Add(copy);
        return Task.FromResult<QuaiMail.Core.Mail.ServerCopy?>(copy);
    }

    public Task DeleteDraftAsync(QuaiMail.Core.Accounts.Account account, QuaiMail.Core.Mail.ServerCopy copy, CancellationToken cancellationToken = default)
    {
        DeletedDrafts.Add(copy);
        return Task.CompletedTask;
    }

    /// <summary>Dossier corbeille du faux serveur (<c>null</c> : pas de corbeille).</summary>
    public string? Trash { get; set; } = "Corbeille";

    public List<(uint Uid, string From, string To)> Moves { get; } = [];

    public List<(string Folder, uint Uid)> PermanentlyDeleted { get; } = [];

    public Task MoveAsync(QuaiMail.Core.Accounts.Account account, string folder, uint uid, string target, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Moves.Add((uid, folder, target));
        return Task.CompletedTask;
    }

    public Task MoveAsync(QuaiMail.Core.Accounts.Account account, string folder, IReadOnlyList<uint> uids, string target, CancellationToken cancellationToken = default)
    {
        if (Batch("move", folder, uids) is { } failure)
        {
            return Task.FromException(failure);
        }

        Moves.AddRange(uids.Select(uid => (uid, folder, target)));
        return Task.CompletedTask;
    }

    public Task<bool> MoveToTrashAsync(QuaiMail.Core.Accounts.Account account, string folder, uint uid, CancellationToken cancellationToken = default)
    {
        if (Trash is null || Trash == folder)
        {
            return Task.FromResult(false);
        }

        Moves.Add((uid, folder, Trash));
        return Task.FromResult(true);
    }

    public Task<bool> MoveToTrashAsync(QuaiMail.Core.Accounts.Account account, string folder, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default)
    {
        if (Batch("trash", folder, uids) is { } failure)
        {
            return Task.FromException<bool>(failure);
        }

        if (Trash is null || Trash == folder)
        {
            return Task.FromResult(false);
        }

        Moves.AddRange(uids.Select(uid => (uid, folder, Trash)));
        return Task.FromResult(true);
    }

    public Task DeletePermanentlyAsync(QuaiMail.Core.Accounts.Account account, string folder, uint uid, CancellationToken cancellationToken = default)
    {
        PermanentlyDeleted.Add((folder, uid));
        return Task.CompletedTask;
    }

    public Task DeletePermanentlyAsync(QuaiMail.Core.Accounts.Account account, string folder, IReadOnlyList<uint> uids, CancellationToken cancellationToken = default)
    {
        if (Batch("delete", folder, uids) is { } failure)
        {
            return Task.FromException(failure);
        }

        PermanentlyDeleted.AddRange(uids.Select(uid => (folder, uid)));
        return Task.CompletedTask;
    }

    public List<QuaiMail.Core.Mail.MessageHeader> ServerSearchResults { get; } = [];

    public Task<IReadOnlyList<QuaiMail.Core.Mail.MessageHeader>> SearchAsync(QuaiMail.Core.Accounts.Account account, string folder, string text, int limit, CancellationToken cancellationToken = default)
    {
        Calls.Add($"search {folder} {text}");
        return Task.FromResult<IReadOnlyList<QuaiMail.Core.Mail.MessageHeader>>([.. ServerSearchResults]);
    }

    public Task DisconnectAsync(string accountId)
    {
        Calls.Add("disconnect " + accountId);
        return Task.CompletedTask;
    }
}

/// <summary>Sélecteur de fichiers scripté.</summary>
public sealed class FakeFileDialogs : QuaiMail.Core.Services.IFileDialogs
{
    public string? Answer { get; set; }

    public List<string> Suggested { get; } = [];

    /// <summary>Le sélecteur de Windows qui échoue (COMException…).</summary>
    public Exception? Failure { get; set; }

    public Task<string?> PickSaveLocationAsync(string suggestedFileName)
    {
        Suggested.Add(suggestedFileName);
        return Failure is null ? Task.FromResult(Answer) : Task.FromException<string?>(Failure);
    }

    public List<string> ToAttach { get; } = [];

    public Task<IReadOnlyList<string>> PickFilesToAttachAsync() => Task.FromResult<IReadOnlyList<string>>([.. ToAttach]);

    public string? KeyFile { get; set; }

    public Task<string?> PickKeyFileAsync() => Task.FromResult(KeyFile);
}

/// <summary>Marque du web factice : note les fichiers marqués, répond le verdict choisi.</summary>
public sealed class FakeAttachmentSafety : QuaiMail.Core.Services.IAttachmentSafety
{
    public QuaiMail.Core.Services.SavedFileVerdict Verdict { get; set; } = QuaiMail.Core.Services.SavedFileVerdict.Marked;

    public List<string> Marked { get; } = [];

    public Task<QuaiMail.Core.Services.SavedFileVerdict> MarkAsync(string path, CancellationToken cancellationToken = default)
    {
        // Le fichier doit être complet quand Windows l'analyse.
        Marked.Add(path + "=" + File.ReadAllText(path));
        return Task.FromResult(Verdict);
    }
}

/// <summary>Navigateur factice : note ce qui aurait été ouvert.</summary>
public sealed class FakeLauncher : QuaiMail.Core.Services.ILauncher
{
    public List<Uri> Opened { get; } = [];

    public bool Open(Uri uri)
    {
        Opened.Add(uri);
        return true;
    }
}

/// <summary>Serveur de clés scripté : les clés publiées, les demandes reçues, une panne à la demande.</summary>
public sealed class FakeKeyServer : QuaiMail.Core.Pgp.IPgpKeyServer
{
    public string Name => "keys.test";

    /// <summary>Clés publiées (armurées), avec l'empreinte ou l'identifiant qui les trouve.</summary>
    public Dictionary<string, string> Published { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = [];

    public bool Unreachable { get; set; }

    public void Publish(Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKeyRing ring, string? armored = null)
    {
        var key = ring.GetPublicKey();
        armored ??= TestPgpKeys.PublicText(ring);
        Published[Convert.ToHexString(key.GetFingerprint())] = armored;
        Published[key.KeyId.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)] = armored;
    }

    public Task<string?> FindAsync(string issuer, CancellationToken cancellationToken = default)
    {
        Requests.Add(issuer);
        return Unreachable
            ? Task.FromException<string?>(new HttpRequestException("Serveur de clés injoignable (test)."))
            : Task.FromResult(Published.GetValueOrDefault(issuer));
    }
}
