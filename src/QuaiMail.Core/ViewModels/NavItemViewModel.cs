using CommunityToolkit.Mvvm.ComponentModel;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.ViewModels;

/// <summary>Une ligne du volet de gauche : un compte (en-tête, repliable) ou l'un de ses dossiers.</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    private NavItemViewModel(string accountId, string title, MailFolderInfo? folder)
    {
        AccountId = accountId;
        Title = title;
        Folder = folder;
        Unread = folder?.Unread ?? 0;
        IsExpanded = true;
    }

    public string AccountId { get; }

    /// <summary>Compte connecté par le navigateur (Gmail, Outlook.com) : « Se reconnecter » est proposé.</summary>
    public bool UsesBrowserSignIn { get; private init; }

    /// <summary>Dossier, ou <c>null</c> pour la ligne du compte.</summary>
    public MailFolderInfo? Folder { get; }

    public string Title { get; }

    public bool IsAccount => Folder is null;

    public int Depth => Folder?.Depth ?? 0;

    public FolderRole Role => Folder?.Role ?? FolderRole.None;

    /// <summary>Une ligne de compte, ou un dossier conteneur (\Noselect), ne montre pas de mails.</summary>
    public bool CanSelect => Folder is { CanSelect: true };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int Unread { get; set; }

    public bool HasUnread => Unread > 0;

    /// <summary>
    /// Ligne d'un compte : ses dossiers sont affichés (vrai) ou repliés. Replié, la ligne montre dans <see cref="Unread"/>
    /// les non lus de ses dossiers (sauf Indésirables et Corbeille) ; déplié, chaque dossier montre les siens.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Identifiant de la boîte de réception unifiée (tous les comptes).</summary>
    public const string UnifiedId = "*";

    public bool IsUnified => AccountId == UnifiedId;

    public static NavItemViewModel ForAccount(string accountId, string title, bool usesBrowserSignIn = false) =>
        new(accountId, title, null) { UsesBrowserSignIn = usesBrowserSignIn };

    /// <summary>Boîte de réception unifiée : les boîtes de réception de tous les comptes, mêlées par date.</summary>
    public static NavItemViewModel ForUnified(string title, int unread) =>
        new(UnifiedId, title, new MailFolderInfo("INBOX", title, 0, FolderRole.Inbox, true, unread, 0));

    public static NavItemViewModel ForFolder(string accountId, MailFolderInfo folder, string title, bool usesBrowserSignIn = false) =>
        new(accountId, title, folder) { UsesBrowserSignIn = usesBrowserSignIn };
}
