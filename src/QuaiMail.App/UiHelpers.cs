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

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity WarningIf(bool value) =>
        value ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

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

    public static Windows.UI.Text.FontWeight StrongIf(bool value) => value ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>Mail non lu : expéditeur et objet en gras.</summary>
    public static Windows.UI.Text.FontWeight StrongIfUnread(bool isRead) => StrongIf(!isRead);

    /// <summary>Nom d'accessibilité d'un mail de la liste (« Non lu, Camille Martin, Réunion de jeudi, 9:30 »).</summary>
    public static string MessageName(string sender, string subject, string date, bool isRead) =>
        Loc.Format(isRead ? "Messages_ItemName" : "Messages_ItemNameUnread", sender, subject, date);
}
