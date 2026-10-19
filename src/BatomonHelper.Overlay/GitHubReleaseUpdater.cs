using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatomonHelper.Overlay;

public sealed record ReleaseUpdate(Version Version, string Tag, Uri InstallerUri, Uri ChecksumUri);

/// <summary>Reads signed-by-hash installer releases published by this application's GitHub repository.</summary>
public sealed class GitHubReleaseUpdater
{
    public const string ReleasesPage = "https://github.com/kamikadzexa/BatomonHelper/releases";
    private static readonly Uri LatestReleaseApi = new("https://api.github.com/repos/kamikadzexa/BatomonHelper/releases/latest");
    private readonly HttpClient _client;

    public GitHubReleaseUpdater(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BatomonHelper", "1.0"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ReleaseUpdate?> FindLatestAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString()?.Trim() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= currentVersion) return null;

        var installerName = $"BatomonHelper-Setup-{FormatVersion(version)}.exe";
        string? installerUrl = null;
        string? checksumUrl = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, installerName, StringComparison.OrdinalIgnoreCase)) installerUrl = url;
            if (string.Equals(name, installerName + ".sha256", StringComparison.OrdinalIgnoreCase)) checksumUrl = url;
        }
        return Uri.TryCreate(installerUrl, UriKind.Absolute, out var installer) && Uri.TryCreate(checksumUrl, UriKind.Absolute, out var checksum)
            ? new ReleaseUpdate(version, tag, installer, checksum)
            : null;
    }

    public async Task<string> DownloadVerifiedInstallerAsync(ReleaseUpdate update, CancellationToken cancellationToken = default)
    {
        var installerName = $"BatomonHelper-Setup-{FormatVersion(update.Version)}.exe";
        var checksum = await _client.GetStringAsync(update.ChecksumUri, cancellationToken);
        var match = Regex.Match(checksum, "^\\s*([a-fA-F0-9]{64})\\s+\\*?" + Regex.Escape(installerName) + "\\s*$", RegexOptions.Multiline);
        if (!match.Success) throw new InvalidDataException("The release checksum file does not match the installer asset.");

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper", "updates");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, installerName);
        var temporary = target + ".download";
        try
        {
            using var response = await _client.GetAsync(update.InstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken);
            string actual;
            await using (var downloaded = File.OpenRead(temporary))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
            if (!string.Equals(actual, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer does not match its published SHA-256 checksum.");
            File.Move(temporary, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
