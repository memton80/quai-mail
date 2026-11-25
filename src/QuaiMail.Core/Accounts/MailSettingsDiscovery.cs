namespace QuaiMail.Core.Accounts;

/// <summary>
/// Réglages pour une adresse : fournisseur connu (sans connexion), sinon autoconfiguration publiée par le fournisseur,
/// sinon supposition à vérifier. Rien n'est enregistré avant qu'une vraie connexion ait réussi.
/// </summary>
public sealed class MailSettingsDiscovery
{
    private readonly ProviderAutoconfig _autoconfig;

    public MailSettingsDiscovery(ProviderAutoconfig autoconfig)
    {
        _autoconfig = autoconfig;
    }

    public async Task<MailSettings?> DiscoverAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!MailAddressRules.IsValidAddress(address))
        {
            return null;
        }

        return KnownProviders.Find(address)
            ?? await _autoconfig.TryGetAsync(address, cancellationToken).ConfigureAwait(false)
            ?? KnownProviders.Guess(address);
    }
}
