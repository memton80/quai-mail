using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using QuaiMail.Core.Mail;

namespace QuaiMail.Core.ViewModels;

/// <summary>Une ligne de la liste des mails (lu / non lu et étoile suivent les changements faits à la lecture).</summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    public MessageItemViewModel(string accountId, string folder, MessageHeader header, string noSubject, DateTimeOffset now)
    {
        AccountId = accountId;
        Folder = folder;
        Header = header;
        Subject = string.IsNullOrWhiteSpace(header.Subject) ? noSubject : header.Subject.Trim();
        DateText = ShortDate(header.Date, now);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRead))]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    public partial MessageHeader Header { get; set; }

    /// <summary>Compte du mail (une recherche ou la boîte de réception unifiée mêlent plusieurs comptes).</summary>
    public string AccountId { get; }

    /// <summary>Dossier du mail.</summary>
    public string Folder { get; }

    public uint Uid => Header.Uid;

    public string Sender => Header.Sender;

    public string Subject { get; }

    public string DateText { get; }

    public bool IsRead => Header.IsRead;

    public bool IsFlagged => Header.IsFlagged;

    /// <summary>Aujourd'hui : l'heure ; cette année : jour et mois ; sinon la date complète (heure locale).</summary>
    public static string ShortDate(DateTimeOffset date, DateTimeOffset now)
    {
        if (date == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var local = date.ToLocalTime();
        var today = now.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        if (local.Date == today.Date)
        {
            return local.ToString("t", culture);
        }

        return local.Year == today.Year
            ? local.ToString("d MMM", culture)
            : local.ToString("d", culture);
    }
}
