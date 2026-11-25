using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuaiMail.Core.Localization;
using QuaiMail.Core.Safety;

namespace QuaiMail.Core.ViewModels;

/// <summary>Une ligne de la liste des expéditeurs sûrs (adresse, ou domaine entier).</summary>
/// <param name="Entry">Valeur enregistrée (adresse ou domaine).</param>
/// <param name="Label">Texte affiché.</param>
/// <param name="RemoveName">Nom d'accessibilité du bouton « Retirer ».</param>
public sealed record TrustedSenderItem(string Entry, string Label, string RemoveName);

/// <summary>Réglages : expéditeurs sûrs (ajoutés depuis la lecture d'un mail, retirés ici).</summary>
public sealed partial class TrustedSendersViewModel : ObservableObject
{
    private readonly TrustedSenders _store;
    private readonly IStrings _strings;

    public TrustedSendersViewModel(TrustedSenders store, IStrings strings)
    {
        _store = store;
        _strings = strings;
        Refresh();
        _store.Changed += (_, _) => Refresh();
    }

    public ObservableCollection<TrustedSenderItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    public void Remove(TrustedSenderItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _store.Remove(item.Entry);
    }

    private void Refresh()
    {
        Items.Clear();
        foreach (var domain in _store.Domains)
        {
            var label = _strings.Format("Trusted_DomainEntry", domain);
            Items.Add(new TrustedSenderItem(domain, label, _strings.Format("Trusted_Remove", label)));
        }

        foreach (var address in _store.Addresses)
        {
            Items.Add(new TrustedSenderItem(address, address, _strings.Format("Trusted_Remove", address)));
        }

        HasItems = Items.Count > 0;
    }
}
