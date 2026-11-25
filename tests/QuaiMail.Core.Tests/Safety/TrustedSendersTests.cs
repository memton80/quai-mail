using QuaiMail.Core.Safety;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;

namespace QuaiMail.Core.Tests.Safety;

public sealed class TrustedSendersTests
{
    [Fact]
    public void An_address_is_trusted_whatever_its_case_and_kept_on_disk()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new TrustedSenders(paths);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        Assert.True(store.TrustAddress(" Camille.Martin@Example.ORG "));
        Assert.False(store.TrustAddress("camille.martin@example.org"));

        Assert.Equal(1, changed);
        Assert.True(store.IsTrusted("CAMILLE.martin@example.org"));
        Assert.False(store.IsTrusted("autre@example.org"));
        var reloaded = new TrustedSenders(paths);
        Assert.Equal(["camille.martin@example.org"], reloaded.Addresses);
        Assert.True(reloaded.IsTrusted("camille.martin@example.org"));
    }

    [Fact]
    public void A_domain_covers_its_subdomains_but_not_its_lookalikes()
    {
        using var temp = new TempDirectory();
        var store = new TrustedSenders(new AppPaths(temp.Path));

        Assert.True(store.TrustDomain("Banque.example"));

        Assert.True(store.IsTrusted("conseiller@banque.example"));
        Assert.True(store.IsTrusted("alertes@mail.banque.example"));
        Assert.False(store.IsTrusted("conseiller@fausse-banque.example"));
        Assert.False(store.IsTrusted("conseiller@banque.example.evil.example"));
    }

    [Theory]
    [InlineData("gmail.com")]
    [InlineData("orange.fr")]
    [InlineData("proton.me")]
    [InlineData("pas un domaine")]
    [InlineData("localhost")]
    public void Open_mail_services_or_invalid_names_cannot_be_trusted_as_a_whole(string domain)
    {
        using var temp = new TempDirectory();
        var store = new TrustedSenders(new AppPaths(temp.Path));

        Assert.False(TrustedSenders.CanTrustDomain(domain));
        Assert.False(store.TrustDomain(domain));
        Assert.Empty(store.Domains);
    }

    [Fact]
    public void International_names_are_compared_in_their_ascii_form()
    {
        using var temp = new TempDirectory();
        var store = new TrustedSenders(new AppPaths(temp.Path));

        Assert.True(store.TrustAddress("léa@café.example"));

        Assert.Equal(["léa@xn--caf-dma.example"], store.Addresses);
        Assert.True(store.IsTrusted("léa@xn--caf-dma.example"));
    }

    [Fact]
    public void An_entry_is_removed_and_an_unreadable_file_is_set_aside()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new TrustedSenders(paths);
        store.TrustAddress("bob@example.org");
        store.TrustDomain("example.net");

        Assert.True(store.Remove("example.net"));
        Assert.True(store.Remove("bob@example.org"));
        Assert.False(store.Remove("bob@example.org"));
        Assert.Empty(new TrustedSenders(paths).Addresses);

        File.WriteAllText(paths.TrustedSendersFile, "{ pas du json");
        Assert.Empty(new TrustedSenders(paths).Domains);
        Assert.Single(Directory.GetFiles(temp.Path, "trusted-senders.json.corrompu-*"));
    }
}
