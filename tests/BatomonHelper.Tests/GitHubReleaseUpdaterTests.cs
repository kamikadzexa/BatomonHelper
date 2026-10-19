using BatomonHelper.Overlay;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BatomonHelper.Tests;

public sealed class GitHubReleaseUpdaterTests
{
    [Fact]
    public async Task Finds_newer_stable_release_with_matching_installer_and_checksum()
    {
        var updater = new GitHubReleaseUpdater(Client((request, _) => JsonResponse("""
            { "tag_name": "v1.2.3", "draft": false, "prerelease": false, "assets": [
              { "name": "BatomonHelper-Setup-1.2.3.exe", "browser_download_url": "https://downloads.example/setup.exe" },
              { "name": "BatomonHelper-Setup-1.2.3.exe.sha256", "browser_download_url": "https://downloads.example/setup.exe.sha256" }
            ] }
            """)));

        var update = await updater.FindLatestAsync(new Version(1, 0, 1));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 3), update.Version);
        Assert.Equal("https://downloads.example/setup.exe", update.InstallerUri.AbsoluteUri);
    }

    [Fact]
    public async Task Downloads_only_when_the_published_sha256_matches()
    {
        var bytes = Encoding.UTF8.GetBytes("verified installer test payload");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var version = new Version(9, 8, 7);
        var updater = new GitHubReleaseUpdater(Client((request, _) => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  BatomonHelper-Setup-9.8.7.exe\n") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var update = new ReleaseUpdate(version, "v9.8.7", new Uri("https://downloads.example/setup.exe"), new Uri("https://downloads.example/setup.exe.sha256"));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper", "updates", "BatomonHelper-Setup-9.8.7.exe");
        try
        {
            var downloaded = await updater.DownloadVerifiedInstallerAsync(update);
            Assert.Equal(path, downloaded);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) => new(new TestHandler(handler));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request, cancellationToken));
    }
}
