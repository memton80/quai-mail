using System.Diagnostics;
using QuaiMail.Core.Pgp;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.Pgp;

/// <summary>
/// Clés de GnuPG (Kleopatra) copiées dans Quai Mail : lecture de la liste de GnuPG, export en mémoire avec la phrase de
/// passe, et la partie « Kleopatra » des Réglages.
/// </summary>
public sealed class GnuPgTests : IDisposable
{
    private const string Passphrase = "phrase de passe d'été";
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly PgpKeyStore _store;

    public GnuPgTests() => _store = new PgpKeyStore(new AppPaths(_temp.Path));

    public void Dispose()
    {
        _store.Dispose();
        _temp.Dispose();
    }

    /// <summary>Sortie de <c>gpg --with-colons --fixed-list-mode --list-secret-keys</c> (GnuPG 2.4), un cas par clé.</summary>
    private const string Listing = """
        sec:u:255:22:A84A581CE477EECF:1795637302:1827173302::u:::scSC:::+::ed25519:::0:
        fpr:::::::::DBDC3DE35A07A4F015300C31A84A581CE477EECF:
        grp:::::::::D996EF0872CB63CF5CF92453548EC62A7EEC0B02:
        uid:r::::1795637302::AAAA::Ancienne <ancienne@example.org>::::::::::0:
        uid:u::::1795637302::6760::Camille L\x3aété <Camille@Example.org>::::::::::0:
        uid:u::::1795637302::6761::Camille <camille@travail.example>::::::::::0:
        ssb:u:255:18:1111111111111111:1795637302::::::e:::+::cv25519::
        fpr:::::::::1111111111111111111111111111111111111111:
        sec:e:255:22:2222222222222222:1700000000:1700000001::u:::scSC:::+::ed25519:::0:
        fpr:::::::::2222222222222222222222222222222222222222:
        uid:e::::1700000000::2222::Expirée <expiree@example.org>::::::::::0:
        sec:r:255:22:3333333333333333:1700000000:::u:::sc:::+::ed25519:::0:
        fpr:::::::::3333333333333333333333333333333333333333:
        uid:r::::1700000000::3333::Révoquée <revoquee@example.org>::::::::::0:
        sec:u:255:22:4444444444444444:1700000000:::u:::cE:::+::ed25519:::0:
        fpr:::::::::4444444444444444444444444444444444444444:
        uid:u::::1700000000::4444::Chiffrement seul <chiffre@example.org>::::::::::0:
        ssb:u:255:18:4545454545454545:1700000000::::::e:::+::cv25519::
        sec:u:4096:1:5555555555555555:1700000000:::u:::cSC:::#::::::0:
        fpr:::::::::5555555555555555555555555555555555555555:
        uid:u::::1700000000::5555::Clé principale hors ligne <horsligne@example.org>::::::::::0:
        ssb:u:4096:1:5656565656565656:1700000000::::::s:::+::::
        sec:u:4096:1:6666666666666666:1700000000:::u:::scSC:::D2760001240103040006123456780000::::::0:
        fpr:::::::::6666666666666666666666666666666666666666:
        uid:u::::1700000000::6666::Carte à puce <carte@example.org>::::::::::0:
        sec:u:4096:1:7777777777777777:1700000000:::u:::scSC:::#::::::0:
        fpr:::::::::7777777777777777777777777777777777777777:
        uid:u::::1700000000::7777::Talon seul <talon@example.org>::::::::::0:
        sec:u:255:22:8888888888888888:1700000000:1795000000::u:::scSC:::+::ed25519:::0:
        fpr:::::::::8888888888888888888888888888888888888888:
        uid:u::::1700000000::8888::Expirée hier <hier@example.org>::::::::::0:
        """;

    [Fact]
    public void Only_keys_that_can_sign_and_whose_secret_is_here_or_on_a_card_are_listed()
    {
        var keys = GnuPgProcess.ParseSecretKeys(Listing.Replace("\n", "\r\n", StringComparison.Ordinal), Now);

        Assert.Equal(
            ["DBDC3DE35A07A4F015300C31A84A581CE477EECF", "5555555555555555555555555555555555555555", "6666666666666666666666666666666666666666"],
            keys.Select(k => k.Fingerprint));

        var camille = keys[0];
        Assert.Equal("Camille L:été <Camille@Example.org>", camille.UserId);
        Assert.Equal(["camille@example.org", "camille@travail.example"], camille.Addresses);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1827173302), camille.Expires);
        Assert.False(camille.OnCard);
        Assert.Equal("DBDC 3DE3 5A07 A4F0 1530 0C31 A84A 581C E477 EECF", camille.DisplayFingerprint);

        // Principale hors ligne mais sous-clé de signature présente : copiable ; sur carte à puce : signalée.
        Assert.False(keys[1].OnCard);
        Assert.Null(keys[1].Expires);
        Assert.True(keys[2].OnCard);
    }

    [Theory]
    [InlineData("[GNUPG:] ERROR export_keys.secret 67108875", GnuPgError.WrongPassphrase)]
    [InlineData("[GNUPG:] ERROR export_keys.secret 67109041", GnuPgError.PassphraseNeeded)]
    [InlineData("[GNUPG:] ERROR export_keys.secret 67108963", GnuPgError.PassphraseNeeded)]
    [InlineData("[GNUPG:] BAD_PASSPHRASE A84A581CE477EECF", GnuPgError.WrongPassphrase)]
    [InlineData("gpg: key 1234: error receiving key from agent: Card error - skipped\n[GNUPG:] ERROR export_keys.secret 67108972", GnuPgError.Failed)]
    [InlineData("", GnuPgError.Failed)]
    public void Export_errors_are_read_from_the_status_lines_whatever_the_language(string status, GnuPgError expected) =>
        Assert.Equal(expected, GnuPgProcess.ExportError(status));

    [Fact]
    public async Task Only_a_fingerprint_can_be_exported()
    {
        var gnupg = new GnuPgProcess("gpg");

        await Assert.ThrowsAsync<ArgumentException>(() => gnupg.ExportSecretKeyAsync("--output=C:\\cle.asc", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Only_full_paths_are_tried_never_the_current_folder()
    {
        Assert.All(GnuPgLocator.Candidates(), path => Assert.True(Path.IsPathFullyQualified(path), path));
    }

    [Fact]
    public async Task Settings_list_kleopatra_keys_and_copy_one_with_its_passphrase()
    {
        var ring = TestPgpKeys.Create("Camille <camille@example.org>", Passphrase);
        var gnupg = new FakeGnuPg(ring, Passphrase);
        var keys = new PgpKeysViewModel(_store, new FakeFileDialogs(), new KeyStrings(), new FixedTime(Now), gnupg: new FakeLocator(gnupg));

        await keys.DetectAsync();

        Assert.True(keys.HasGnuPg);
        Assert.Equal("Pgp_FoundKleopatra", keys.GnuPgTitle);
        var item = Assert.Single(keys.GnuPgKeys);
        Assert.True(item.CanImport);
        Assert.Equal("Pgp_GnuPgImport", item.ActionText);
        Assert.Equal("Pgp_GnuPgPassphrase(Camille <camille@example.org>,Pgp_Kleopatra)", item.PassphrasePrompt);

        // Sans phrase de passe, puis une mauvaise : rien n'est copié, l'erreur est dite.
        Assert.False(await keys.ImportFromGnuPgAsync(item, null));
        Assert.Equal("GnuPgError_PassphraseNeeded", keys.Message);
        Assert.False(await keys.ImportFromGnuPgAsync(item, "pas la bonne"));
        Assert.Equal("GnuPgError_WrongPassphrase", keys.Message);
        Assert.True(keys.IsError);
        Assert.Empty(_store.Keys);

        Assert.True(await keys.ImportFromGnuPgAsync(item, Passphrase));

        var copied = Assert.Single(_store.Keys);
        Assert.Equal(gnupg.Fingerprint, copied.Fingerprint);
        Assert.True(copied.NeedsPassphrase);
        Assert.False(keys.IsError);
        Assert.Equal("Pgp_GnuPgAlready", Assert.Single(keys.GnuPgKeys).Status);
        Assert.False(keys.GnuPgKeys[0].CanImport);
        Assert.Equal([null, "pas la bonne", Passphrase], gnupg.Passphrases);

        // La copie reste dans Quai Mail, même si GnuPG disparaît (Kleopatra désinstallé).
        var without = new PgpKeysViewModel(_store, new FakeFileDialogs(), new KeyStrings(), new FixedTime(Now), gnupg: new FakeLocator(null));
        await without.DetectAsync();
        Assert.False(without.HasGnuPg);
        Assert.Single(without.Keys);
    }

    [Fact]
    public async Task A_key_extended_in_kleopatra_can_be_updated_and_a_card_key_cannot_be_copied()
    {
        var ring = TestPgpKeys.Create("Camille <camille@example.org>", Passphrase);
        using (var armored = TestPgpKeys.Armored(ring))
        {
            _store.Import(armored);
        }

        var gnupg = new FakeGnuPg(ring, Passphrase) { Expires = Now.AddYears(2), Extra = [new GnuPgKey(new string('6', 40), "Carte <carte@example.org>", [], null, OnCard: true)] };
        var keys = new PgpKeysViewModel(_store, new FakeFileDialogs(), new KeyStrings(), new FixedTime(Now), gnupg: new FakeLocator(gnupg));

        await keys.DetectAsync();

        var extended = keys.GnuPgKeys[0];
        Assert.True(extended.CanImport);
        Assert.Equal("Pgp_GnuPgUpdate", extended.ActionText);
        Assert.StartsWith("Pgp_GnuPgChanged(Pgp_Expires(", extended.Status, StringComparison.Ordinal);
        Assert.False(keys.GnuPgKeys[1].CanImport);
        Assert.Equal("Pgp_GnuPgOnCard", keys.GnuPgKeys[1].Status);
        Assert.False(await keys.ImportFromGnuPgAsync(keys.GnuPgKeys[1], Passphrase));
    }

    [Fact]
    public async Task An_unresponsive_gnupg_is_reported_and_the_file_import_stays()
    {
        var keys = new PgpKeysViewModel(_store, new FakeFileDialogs(), new KeyStrings(), new FixedTime(Now), gnupg: new FakeLocator(new BrokenGnuPg()));

        await keys.DetectAsync();

        Assert.True(keys.HasGnuPg);
        Assert.False(keys.HasGnuPgKeys);
        Assert.Equal("GnuPgError_Failed", keys.Message);
        Assert.True(keys.IsError);
    }

    /// <summary>
    /// Avec le vrai GnuPG (s'il est installé et utilisable) : clés créées par gpg dans un trousseau jetable, listées,
    /// exportées sans phrase (refusé), avec une mauvaise (refusé), puis la bonne, et copiées dans Quai Mail.
    /// </summary>
    [Fact]
    public async Task With_the_real_gnupg_a_key_is_listed_then_copied_only_with_its_passphrase()
    {
        if (Gpg(null, "--version").ExitCode != 0)
        {
            Assert.Skip("GnuPG absent : test non lancé.");
        }

        using var home = new TempDirectory();
        const string phrase = "clé d'été à Noël";
        try
        {
            var generated = Gpg(home.Path, "--batch", "--pinentry-mode", "loopback", "--passphrase", phrase, "--quick-gen-key", "Camille <camille@example.org>", "ed25519", "sign", "1y");
            if (generated.ExitCode != 0)
            {
                Assert.Skip("GnuPG présent mais inutilisable ici : " + generated.Output.Trim().Split('\n')[0]);
            }

            Gpg(home.Path, "--batch", "--pinentry-mode", "loopback", "--passphrase", string.Empty, "--quick-gen-key", "Sans Phrase <libre@example.org>", "ed25519", "sign", "never");
            Gpg(home.Path, "--batch", "--pinentry-mode", "loopback", "--passphrase", "x", "--quick-gen-key", "Chiffre <chiffre@example.org>", "cv25519", "encr", "1y");

            // Plus aucune phrase en mémoire chez gpg-agent.
            Gpgconf(home.Path, "--kill", "gpg-agent");
            var gnupg = new GnuPgProcess("gpg", homeDirectory: home.Path);
            var ct = TestContext.Current.CancellationToken;

            var listed = await gnupg.ListSecretKeysAsync(ct);

            Assert.Equal(["Camille <camille@example.org>", "Sans Phrase <libre@example.org>"], listed.Select(k => k.UserId).Order());
            var camille = listed.Single(k => k.Addresses.Contains("camille@example.org"));
            Assert.Equal(GnuPgError.PassphraseNeeded, (await Assert.ThrowsAsync<GnuPgException>(() => gnupg.ExportSecretKeyAsync(camille.Fingerprint, null, ct))).Error);
            Assert.Equal(GnuPgError.WrongPassphrase, (await Assert.ThrowsAsync<GnuPgException>(() => gnupg.ExportSecretKeyAsync(camille.Fingerprint, "mauvaise", ct))).Error);

            var armored = await gnupg.ExportSecretKeyAsync(camille.Fingerprint, phrase, ct);
            var copied = Assert.Single(_store.Import(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(armored))));
            Assert.Equal(camille.Fingerprint, copied.Fingerprint);
            Assert.True(copied.NeedsPassphrase);
            Assert.Equal(camille.Expires, copied.Expires);

            // Clé publique d'un correspondant (vérifier ses mails signés) : sans phrase de passe, par identifiant ou empreinte.
            Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", await gnupg.ExportPublicKeyAsync(camille.Fingerprint[^16..], ct), StringComparison.Ordinal);
            Assert.Null(await gnupg.ExportPublicKeyAsync(new string('A', 40), ct));

            // Une clé sans phrase de passe se copie sans rien demander.
            var free = listed.Single(k => k.Addresses.Contains("libre@example.org"));
            Assert.False(Assert.Single(_store.Import(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(await gnupg.ExportSecretKeyAsync(free.Fingerprint, null, ct))))).NeedsPassphrase);
        }
        finally
        {
            Gpgconf(home.Path, "--kill", "gpg-agent");
        }
    }

    private static (int ExitCode, string Output) Gpg(string? home, params string[] arguments) =>
        Run("gpg", home is null ? arguments : ["--homedir", home, .. arguments]);

    private static (int ExitCode, string Output) Gpgconf(string home, params string[] arguments) => Run("gpgconf", ["--homedir", home, .. arguments]);

    private static (int ExitCode, string Output) Run(string program, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit(60_000);
            return (process.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeLocator(IGnuPg? gnupg) : IGnuPgLocator
    {
        public IGnuPg? Find() => gnupg;
    }

    /// <summary>GnuPG scripté : une clé (créée pour le test), exportée seulement avec sa phrase de passe.</summary>
    private sealed class FakeGnuPg(Org.BouncyCastle.Bcpg.OpenPgp.PgpSecretKeyRing ring, string passphrase) : IGnuPg
    {
        public string Fingerprint { get; } = Convert.ToHexString(ring.GetPublicKey().GetFingerprint());

        public DateTimeOffset? Expires { get; init; }

        public IReadOnlyList<GnuPgKey> Extra { get; init; } = [];

        public List<string?> Passphrases { get; } = [];

        public bool HasKleopatra => true;

        public Task<IReadOnlyList<GnuPgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GnuPgKey>>([new GnuPgKey(Fingerprint, "Camille <camille@example.org>", ["camille@example.org"], Expires, false), .. Extra]);

        public Task<string> ExportSecretKeyAsync(string fingerprint, string? given, CancellationToken cancellationToken = default)
        {
            Passphrases.Add(given);
            if (given != passphrase)
            {
                return Task.FromException<string>(new GnuPgException(given is null ? GnuPgError.PassphraseNeeded : GnuPgError.WrongPassphrase));
            }

            using var armored = TestPgpKeys.Armored(ring);
            return Task.FromResult(System.Text.Encoding.ASCII.GetString(armored.ToArray()));
        }

        public Task<string?> ExportPublicKeyAsync(string issuer, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class BrokenGnuPg : IGnuPg
    {
        public bool HasKleopatra => false;

        public Task<IReadOnlyList<GnuPgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<GnuPgKey>>(new GnuPgException(GnuPgError.Failed));

        public Task<string> ExportSecretKeyAsync(string fingerprint, string? passphrase, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new GnuPgException(GnuPgError.Failed));

        public Task<string?> ExportPublicKeyAsync(string issuer, CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new GnuPgException(GnuPgError.Failed));
    }
}
