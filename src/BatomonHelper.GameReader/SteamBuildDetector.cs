using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BatomonHelper.GameReader;

public sealed record SteamInstall(string GameDirectory, string BuildId, string ExecutableSha256);

public static partial class SteamBuildDetector
{
    public static async Task<SteamInstall?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var steamRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return null;
        var text = await File.ReadAllTextAsync(vdf, cancellationToken);
        var libraries = LibraryPathRegex().Matches(text).Select(x => x.Groups["path"].Value.Replace("\\\\", "\\")).Append(steamRoot).Distinct();
        foreach (var library in libraries)
        {
            var manifest = Path.Combine(library, "steamapps", "appmanifest_4557380.acf");
            if (!File.Exists(manifest)) continue;
            var manifestText = await File.ReadAllTextAsync(manifest, cancellationToken);
            var build = BuildRegex().Match(manifestText).Groups["build"].Value;
            var installDirectory = InstallRegex().Match(manifestText).Groups["directory"].Value;
            var gameDirectory = Path.Combine(library, "steamapps", "common", installDirectory);
            var executable = Path.Combine(gameDirectory, "batomon_showdown.exe");
            if (string.IsNullOrWhiteSpace(build) || !File.Exists(executable)) continue;
            await using var stream = File.OpenRead(executable);
            return new(gameDirectory, build, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        return null;
    }

    [GeneratedRegex("\\\"path\\\"\\s*\\\"(?<path>[^\\\"]+)\\\"")] private static partial Regex LibraryPathRegex();
    [GeneratedRegex("\\\"buildid\\\"\\s*\\\"(?<build>\\d+)\\\"")] private static partial Regex BuildRegex();
    [GeneratedRegex("\\\"installdir\\\"\\s*\\\"(?<directory>[^\\\"]+)\\\"")] private static partial Regex InstallRegex();
}
