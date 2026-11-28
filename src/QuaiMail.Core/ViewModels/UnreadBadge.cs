namespace QuaiMail.Core.ViewModels;

/// <summary>Texte de la pastille des non lus sur l'icône de la barre des tâches (comme Discord : « 9+ » au-delà de 9).</summary>
public static class UnreadBadge
{
    /// <summary>Au-delà, la pastille montre « 9+ » : elle ne fait que 16 pixels à l'échelle 100 %.</summary>
    public const int MaxShown = 9;

    /// <summary>Rien (pas de pastille) sans non lu ou réglage coupé, sinon « 1 » à « 9 », puis « 9+ ».</summary>
    public static string? Text(int unread, bool enabled = true) => !enabled || unread <= 0
        ? null
        : unread > MaxShown
            ? $"{MaxShown}+"
            : unread.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
