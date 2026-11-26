using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using QuaiMail.App.Localization;
using QuaiMail.Core.Mail;

namespace QuaiMail.App;

/// <summary>Fonctions utilisées depuis le XAML (<c>x:Bind</c>).</summary>
public static class UiHelpers
{
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>Visible seulement si les deux conditions sont vraies (ex. bouton Actualiser : comptes présents et volet déployé).</summary>
    public static Visibility VisibleIfBoth(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Nom du bouton « menu » du volet : l'action qu'il déclenchera.</summary>
    public static string PaneToggleName(bool isCompact) => Loc.Get(isCompact ? "Pane_Expand" : "Pane_Collapse");

    /// <summary>Nom (et info-bulle) d'une ligne du rail d'icônes : titre, et non lus s'il y en a.</summary>
    public static string RailItemName(string title, int unread) =>
        unread > 0 ? Loc.Format("Nav_RailItemUnread", title, unread) : title;

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity WarningIf(bool value) =>
        value ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    /// <summary>Bandeau du mode méfiant : information (inconnu), avertissement (indices), erreur (peut-être usurpé).</summary>
    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity SafetySeverity(Core.Safety.SenderStatus status) => status switch
    {
        Core.Safety.SenderStatus.Spoofed => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
        Core.Safety.SenderStatus.Suspicious => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
        _ => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
    };

    /// <summary>Bandeau de la signature OpenPGP : vert si vérifiée, orange si douteuse, rouge si fausse.</summary>
    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity SignatureSeverity(Core.ViewModels.SignatureLevel level) => level switch
    {
        Core.ViewModels.SignatureLevel.Good => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
        Core.ViewModels.SignatureLevel.Warning => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
        Core.ViewModels.SignatureLevel.Bad => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
        _ => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
    };

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity ErrorIf(bool value) =>
        value ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    public static string VersionText(string version) => Loc.Format("Settings_Version", version);

    /// <summary>Retrait d'une ligne du volet de gauche : les dossiers sous leur compte, les sous-dossiers sous leur parent.</summary>
    public static Thickness Indent(int depth, bool isAccount) => new(isAccount ? 0 : 12 + (depth * 16), 0, 0, 0);

    /// <summary>Icône (Segoe Fluent Icons) d'un compte ou d'un dossier selon son rôle.</summary>
    public static string FolderGlyph(FolderRole role, bool isAccount) => isAccount ? "" : role switch
    {
        FolderRole.Inbox => "",
        FolderRole.Drafts => "",
        FolderRole.Sent => "",
        FolderRole.Archive => "",
        FolderRole.Junk => "",
        FolderRole.Trash => "",
        _ => "",
    };

    /// <summary>Flèche d'une ligne de compte : vers le bas, dossiers affichés ; vers la droite, compte replié.</summary>
    public static string ChevronGlyph(bool isExpanded) => isExpanded ? "\uE70D" : "\uE76C";

    /// <summary>Nom d'accessibilité d'une ligne du volet : un compte dit s'il est replié, et ses non lus cachés.</summary>
    public static string NavItemName(string title, bool isAccount, bool isExpanded, int unread) =>
        !isAccount ? title
        : isExpanded ? Loc.Format("Nav_AccountExpanded", title)
        : unread > 0 ? Loc.Format("Nav_AccountCollapsedUnread", title, unread)
        : Loc.Format("Nav_AccountCollapsed", title);

    public static Windows.UI.Text.FontWeight StrongIf(bool value) => value ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>Mail non lu : expéditeur et objet en gras.</summary>
    public static Windows.UI.Text.FontWeight StrongIfUnread(bool isRead) => StrongIf(!isRead);

    /// <summary>Nom d'accessibilité d'un mail de la liste (« Non lu, Camille Martin, Réunion de jeudi, 9:30 »).</summary>
    public static string MessageName(string sender, string subject, string date, bool isRead) =>
        Loc.Format(isRead ? "Messages_ItemName" : "Messages_ItemNameUnread", sender, subject, date);

    public static string Recipients(string to) => Loc.Format("Reader_To", to);

    public static string CopyRecipients(string cc) => Loc.Format("Reader_Cc", cc);

    public static string ReadToggleText(bool isRead) => Loc.Get(isRead ? "Reader_MarkUnread" : "Reader_MarkRead");

    /// <summary>Enveloppe fermée pour « marquer non lu », ouverte pour « marquer lu ».</summary>
    public static string ReadGlyph(bool isRead) => isRead ? "\uE715" : "\uE8C3";

    public static string FlagToggleText(bool isFlagged) => Loc.Get(isFlagged ? "Reader_Unflag" : "Reader_Flag");

    public static string FlagGlyph(bool isFlagged) => isFlagged ? "\uE735" : "\uE734";

    public static string BlockedImagesText(int count) => Loc.Format("Reader_BlockedImages", count);

    public static string AttachmentGlyph(bool isExecutable) => isExecutable ? "\uE7BA" : "\uE723";

    public static string AttachmentName(string fileName, long size, bool isExecutable) =>
        Loc.Format(isExecutable ? "Attachment_NameDanger" : "Attachment_Name", fileName, FileSize(size));

    /// <summary>Taille lisible (« 820 o », « 12 Ko », « 3,4 Mo »).</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => Loc.Format("Size_Bytes", bytes),
        < 1024 * 1024 => Loc.Format("Size_Kilobytes", Math.Max(1, (int)Math.Round(bytes / 1024.0))),
        _ => Loc.Format("Size_Megabytes", Math.Round(bytes / (1024.0 * 1024.0), 1)),
    };

    /// <summary>« Alice Martin (alice@example.org) », ou l'adresse seule.</summary>
    public static string AccountLine(string title, string address) =>
        string.Equals(title, address, StringComparison.OrdinalIgnoreCase) ? address : $"{title} ({address})";

    public static string RemoveAttachmentName(string fileName) => Loc.Format("Composer_RemoveAttachmentName", fileName);

    public static bool IsSet(object? value) => value is not null;
}
