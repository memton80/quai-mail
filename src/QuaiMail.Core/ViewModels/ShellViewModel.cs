using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuaiMail.Core.Drafts;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Models;
using QuaiMail.Core.Services;
using QuaiMail.Core.Storage;

namespace QuaiMail.Core.ViewModels;

/// <summary>
/// La fenêtre principale : compte et dossier sélectionnés, mail ouvert, brouillon en cours. Garde le brouillon avant de
/// quitter ou de se mettre à jour (« Garder ce brouillon ? »), note l'état de la fenêtre avant une mise à jour et le
/// restaure une fois dans la nouvelle version.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IStrings _strings;
    private readonly DraftStore _drafts;
    private readonly WindowStateStore _windowState;
    private readonly ILogger _logger;

    public ShellViewModel(
        IDialogService dialogs,
        IStrings strings,
        DraftStore drafts,
        WindowStateStore windowState,
        ILogger<ShellViewModel>? logger = null)
    {
        _dialogs = dialogs;
        _strings = strings;
        _drafts = drafts;
        _windowState = windowState;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Compte sélectionné (identifiant interne).</summary>
    [ObservableProperty]
    public partial string? SelectedAccountId { get; set; }

    /// <summary>Dossier affiché dans la liste des mails.</summary>
    [ObservableProperty]
    public partial string? SelectedFolderPath { get; set; }

    /// <summary>Mail ouvert dans le volet de lecture.</summary>
    [ObservableProperty]
    public partial string? OpenMessageId { get; set; }

    /// <summary>Brouillon en cours d'écriture, s'il y en a un.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraft))]
    [NotifyPropertyChangedFor(nameof(DraftTitle))]
    public partial Draft? CurrentDraft { get; set; }

    public bool HasDraft => CurrentDraft is not null;

    /// <summary>Objet du brouillon en cours, ou « (sans objet) ».</summary>
    public string DraftTitle =>
        CurrentDraft is { Subject: { } subject } && !string.IsNullOrWhiteSpace(subject) ? subject.Trim() : _strings.Get("Draft_NoSubject");

    /// <summary>Ce que montre la fenêtre (identifiants seulement).</summary>
    public WindowState CaptureState() => new()
    {
        AccountId = SelectedAccountId,
        FolderPath = SelectedFolderPath,
        MessageId = OpenMessageId,
        DraftId = CurrentDraft?.Id,
    };

    /// <summary>Ouvre un brouillon dans la fenêtre (nouveau message, brouillon repris…).</summary>
    public void OpenDraft(Draft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        CurrentDraft = draft;
    }

    /// <summary>
    /// Avant de fermer la fenêtre ou de se mettre à jour : un brouillon non vide est proposé à « Garder » (enregistré sur
    /// cet ordinateur), « Supprimer » ou « Annuler ». Un brouillon vide est simplement retiré.
    /// </summary>
    /// <returns><c>false</c> si l'utilisateur annule, ou si le brouillon n'a pas pu être enregistré : rien n'est perdu.</returns>
    public async Task<bool> ConfirmCloseDraftAsync()
    {
        if (CurrentDraft is not { } draft)
        {
            return true;
        }

        if (draft.IsEmpty)
        {
            DeleteQuietly(draft.Id);
            CurrentDraft = null;
            return true;
        }

        switch (await _dialogs.AskKeepDraftAsync(DraftTitle))
        {
            case DraftChoice.Keep:
                try
                {
                    _drafts.Save(draft);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Jamais de brouillon perdu : on reste là, l'utilisateur est prévenu.
                    _logger.LogError("Brouillon {Id} impossible à enregistrer ({Error})", draft.Id, ex.GetType().Name);
                    await _dialogs.ShowErrorAsync(_strings.Get("Draft_SaveFailedTitle"), _strings.Get("Draft_SaveFailedMessage"));
                    return false;
                }

                _logger.LogInformation("Brouillon {Id} gardé sur cet ordinateur", draft.Id);
                return true;

            case DraftChoice.Discard:
                DeleteQuietly(draft.Id);
                CurrentDraft = null;
                _logger.LogInformation("Brouillon {Id} supprimé", draft.Id);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Juste avant de lancer l'installateur d'une mise à jour : brouillon gardé (ou non, au choix de l'utilisateur), puis
    /// état de la fenêtre noté dans <c>updates/reopen.txt</c>.
    /// </summary>
    /// <returns><c>false</c> si l'utilisateur annule : la mise à jour attend, déjà téléchargée.</returns>
    public async Task<bool> PrepareForUpdateAsync()
    {
        if (!await ConfirmCloseDraftAsync())
        {
            return false;
        }

        try
        {
            _windowState.SaveForUpdate(CaptureState());
            _logger.LogInformation("État de la fenêtre noté avant la mise à jour");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // La mise à jour passe avant : le brouillon est déjà gardé, seule la fenêtre ne sera pas restaurée.
            _logger.LogWarning("État de la fenêtre non noté avant la mise à jour ({Error})", ex.GetType().Name);
        }

        return true;
    }

    /// <summary>Nouvelle version lancée par l'installateur (<c>--after-update</c>) : restaure l'état noté, une seule fois.</summary>
    /// <returns><c>true</c> si un état a été restauré.</returns>
    public bool RestoreAfterUpdate()
    {
        if (_windowState.TakeAfterUpdate() is not { } state)
        {
            _logger.LogInformation("Aucun état de la fenêtre à restaurer après la mise à jour");
            return false;
        }

        SelectedAccountId = state.AccountId;
        SelectedFolderPath = state.FolderPath;
        OpenMessageId = state.MessageId;

        var draftRestored = false;
        if (state.DraftId is { } id)
        {
            if (_drafts.Load(id) is { } draft)
            {
                CurrentDraft = draft;
                draftRestored = true;
                _logger.LogInformation("Brouillon rouvert après la mise à jour");
            }
            else
            {
                _logger.LogWarning("Brouillon {Id} à rouvrir après la mise à jour introuvable", id);
            }
        }

        _logger.LogInformation(
            "État de la fenêtre restauré après la mise à jour (compte : {Account}, dossier : {Folder}, mail : {Message}, brouillon : {Draft})",
            Has(state.AccountId),
            Has(state.FolderPath),
            Has(state.MessageId),
            draftRestored ? "oui" : "non");
        return true;
    }

    private static string Has(string? value) => value is null ? "non" : "oui";

    private void DeleteQuietly(string id)
    {
        try
        {
            _drafts.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Brouillon {Id} impossible à supprimer ({Error})", id, ex.GetType().Name);
        }
    }
}
