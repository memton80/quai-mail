using QuaiMail.Core.Pgp;

namespace QuaiMail.App.Services;

/// <summary>
/// GnuPG du test de fumée : jamais le vrai trousseau de l'ordinateur (ni celui du CI). Il « contient » la clé de test
/// que le test lui confie, et l'exporte seulement avec la bonne phrase de passe, comme GnuPG en mode loopback.
/// </summary>
internal sealed class SmokeGnuPgLocator : IGnuPgLocator
{
    /// <summary>Clé de test (empreinte, identité, export armuré) ; <c>null</c> : GnuPG « absent ».</summary>
    public (string Fingerprint, string UserId, string Armored)? Key { get; set; }

    /// <summary>Exports demandés (avec ou sans phrase de passe).</summary>
    public int Exports { get; private set; }

    public IGnuPg? Find() => Key is { } key ? new SmokeGnuPg(this, key) : null;

    private sealed class SmokeGnuPg(SmokeGnuPgLocator owner, (string Fingerprint, string UserId, string Armored) key) : IGnuPg
    {
        public bool HasKleopatra => true;

        public Task<IReadOnlyList<GnuPgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GnuPgKey>>([new GnuPgKey(key.Fingerprint, key.UserId, [], null, OnCard: false)]);

        public Task<string> ExportSecretKeyAsync(string fingerprint, string? passphrase, CancellationToken cancellationToken = default)
        {
            owner.Exports++;
            return passphrase switch
            {
                null => Task.FromException<string>(new GnuPgException(GnuPgError.PassphraseNeeded)),
                SmokePgp.Passphrase when fingerprint == key.Fingerprint => Task.FromResult(key.Armored),
                _ => Task.FromException<string>(new GnuPgException(GnuPgError.WrongPassphrase)),
            };
        }

        /// <summary>Aucune clé de correspondant dans ce Kleopatra-là.</summary>
        public Task<string?> ExportPublicKeyAsync(string issuer, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
