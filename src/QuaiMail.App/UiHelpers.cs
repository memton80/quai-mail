using Microsoft.UI.Xaml;
using QuaiMail.App.Localization;

namespace QuaiMail.App;

/// <summary>Fonctions utilisées depuis le XAML (<c>x:Bind</c>).</summary>
public static class UiHelpers
{
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity WarningIf(bool value) =>
        value ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    public static string VersionText(string version) => Loc.Format("Settings_Version", version);
}
