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

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) =>
        Task.FromResult(ConfirmAnswer);

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

    public Task DisconnectAsync(string accountId)
    {
        Calls.Add("disconnect " + accountId);
        return Task.CompletedTask;
    }
}
