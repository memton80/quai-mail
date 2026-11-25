using System.Net;
using System.Text;
using System.Text.Json;
using QuaiMail.Core.Updates;

namespace QuaiMail.Core.Tests.Updates;

/// <summary>
/// Preuve sur la vraie chaîne : la réponse réelle de l'API GitHub (<c>releases/latest</c> de memton80/quai-mail, lue
/// sans jeton et enregistrée telle quelle dans <c>Fixtures/</c>) est lue par <see cref="GitHubReleaseSource"/>, et
/// <see cref="UpdateChecker"/> y voit la version publiée comme plus récente que la précédente, avec l'installateur de
/// chaque architecture et son empreinte SHA-256 (le <c>digest</c> calculé par GitHub).
/// </summary>
public sealed class PublishedReleaseTests
{
    private const string Fixture = "releases-latest-v0.1.1.json";

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "QuaiMail-Setup-x64.exe")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "QuaiMail-Setup-arm64.exe")]
    public async Task The_real_answer_for_0_1_1_is_an_update_from_0_1_0_with_a_verifiable_installer(
        System.Runtime.InteropServices.Architecture architecture, string installer)
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Updates", "Fixtures", Fixture));
        var expected = PublishedAsset(body, installer);
        using var source = new GitHubReleaseSource(new FixedAnswer(body));
        var checker = new UpdateChecker(source, "0.1.0", UpdateChecker.InstallerNameFor(architecture));

        var update = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.Equal("0.1.1", update.Version);
        Assert.Equal("https://github.com/memton80/quai-mail/releases/tag/v0.1.1", update.ReleasePage.AbsoluteUri);
        var package = Assert.IsType<UpdatePackage>(update.Installer);
        Assert.Equal(installer, package.FileName);
        Assert.Equal(expected.Size, package.Size);
        Assert.Equal(expected.Sha256, package.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", package.Sha256);

        // L'adresse construite par l'application est bien celle que GitHub publie pour ce fichier.
        Assert.Equal(expected.DownloadUrl, package.DownloadUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("0.1.1")]
    [InlineData("0.2.0")]
    public async Task The_real_answer_offers_nothing_to_an_installation_already_up_to_date(string current)
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Updates", "Fixtures", Fixture));
        using var source = new GitHubReleaseSource(new FixedAnswer(body));

        Assert.Null(await new UpdateChecker(source, current, "QuaiMail-Setup-x64.exe").CheckAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Même vérification en direct sur l'API publique de GitHub, sans jeton (lancée à la demande :
    /// <c>QUAI_MAIL_LIVE_TESTS=1</c>, jamais par défaut pour ne pas dépendre du réseau).
    /// </summary>
    [Fact]
    public async Task Live_the_latest_published_release_is_newer_than_0_1_0()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("QUAI_MAIL_LIVE_TESTS") == "1", "Test en direct : QUAI_MAIL_LIVE_TESTS=1 pour le lancer.");
        using var source = new GitHubReleaseSource();

        var update = await new UpdateChecker(source, "0.1.0", "QuaiMail-Setup-x64.exe").CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(update);
        Assert.NotNull(update.Installer);
        Assert.Matches("^[0-9a-f]{64}$", update.Installer.Sha256);
    }

    private static (long Size, string Sha256, string DownloadUrl) PublishedAsset(string body, string name)
    {
        using var document = JsonDocument.Parse(body);
        var asset = document.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
        var digest = asset.GetProperty("digest").GetString()!;
        Assert.StartsWith("sha256:", digest, StringComparison.Ordinal);
        return (asset.GetProperty("size").GetInt64(), digest["sha256:".Length..], asset.GetProperty("browser_download_url").GetString()!);
    }

    private sealed class FixedAnswer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
