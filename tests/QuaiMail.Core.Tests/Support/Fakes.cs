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
