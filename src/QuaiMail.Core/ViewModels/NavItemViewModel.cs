using CommunityToolkit.Mvvm.ComponentModel;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.ViewModels;

/// <summary>Une ligne du volet de gauche : un compte (en-tête) ou l'un de ses dossiers.</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    private NavItemViewModel(string accountId, string title, MailFolderInfo? folder)
    {
        AccountId = accountId;
        Title = title;
        Folder = folder;
        Unread = folder?.Unread ?? 0;
    }

    public string AccountId { get; }

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

    public static NavItemViewModel ForAccount(string accountId, string title) => new(accountId, title, null);

    public static NavItemViewModel ForFolder(string accountId, MailFolderInfo folder, string title) => new(accountId, title, folder);
}
