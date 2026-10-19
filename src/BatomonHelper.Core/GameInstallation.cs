using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatomonHelper.Core;

public sealed record GameReadiness(bool GameFound, bool Running, bool LoaderReady, bool BridgeInstalled,
    bool BridgeCurrent, bool CatalogReady, bool ConfigurationConflict)
{
    public bool CanLaunch => GameFound && LoaderReady && BridgeCurrent && CatalogReady && !Running;
    public string Summary => !GameFound ? "Select the folder containing Batomon Showdown."
        : ConfigurationConflict ? "A different ModLoader is configured. Resolve override.cfg before installing."
        : !LoaderReady ? "Install the community Mod Loader and helper bridge."
        : !BridgeInstalled ? "Install the helper bridge."
        : !BridgeCurrent ? "Update or repair the helper bridge."
        : !CatalogReady ? "The bundled catalog is missing. Repair the helper installation."
        : Running ? "Game running. Waiting for the bridge connection."
        : "Ready. Launch the game to connect the helper.";
}

/// <summary>Manages only the companion bridge, missing loader files, and its autoload entry.</summary>
public sealed class GameInstallation
{
    public const string SteamAppId = "4557380";
    public const string LoaderDownload = "https://www.nexusmods.com/batomonshowdown/mods/1?tab=files";
    public static readonly string[] LoaderFiles = ["mod_loader.gd", "mod_settings_menu.gd", "mod_title_state.gd"];
    private static readonly string[] BridgeFiles = ["mod.json", "mod_main.gd"];
    private const string Autoload = "ModLoader=\"*res://Mods/mod_loader.gd\"";
    private readonly Func<bool> _running;
    private readonly Action<string, byte[]> _write;
    private readonly string _bundle;

    public GameInstallation(string bundleDirectory, Func<bool>? running = null, Action<string, byte[]>? write = null)
    {
        _bundle = bundleDirectory;
        _running = running ?? IsGameRunning;
        _write = write ?? File.WriteAllBytes;
    }

    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("batomon_showdown");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static bool IsGameDirectory(string? directory) => !string.IsNullOrWhiteSpace(directory)
        && File.Exists(Path.Combine(directory, "batomon_showdown.exe"))
        && File.Exists(Path.Combine(directory, "batomon_showdown.pck"));

    public static bool IsLoaderDirectory(string? directory) => !string.IsNullOrWhiteSpace(directory)
        && LoaderFiles.All(name => File.Exists(Path.Combine(directory, "Mods", name)) || File.Exists(Path.Combine(directory, name)));

    public GameReadiness Inspect(string? directory)
    {
        var found = IsGameDirectory(directory);
        var config = found && File.Exists(Path.Combine(directory!, "override.cfg"))
            ? File.ReadAllText(Path.Combine(directory!, "override.cfg")) : "";
        var entries = Regex.Matches(config, @"(?m)^\s*ModLoader\s*=.*$");
        var correct = entries.Count == 1 && Regex.IsMatch(entries[0].Value.Trim(), "^ModLoader\\s*=\\s*\"\\*res://Mods/mod_loader\\.gd\"\\s*$")
            && Regex.IsMatch(config, @"(?ms)^\s*\[autoload\]\s*\r?$\s*(?:(?!^\s*\[).)*?^\s*ModLoader\s*=");
        var installed = found && BridgeFiles.All(name => File.Exists(Path.Combine(directory!, "Mods", "BatomonHelperBridge", name)));
        var current = installed && BridgeFiles.All(name =>
            File.Exists(Path.Combine(_bundle, "mods", "BatomonHelperBridge", name)) &&
            File.ReadAllBytes(Path.Combine(directory!, "Mods", "BatomonHelperBridge", name))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(_bundle, "mods", "BatomonHelperBridge", name))));
        return new(found, _running(), found && correct && LoaderFiles.All(name => File.Exists(Path.Combine(directory!, "Mods", name))),
            installed, current, File.Exists(Path.Combine(_bundle, "assets", "wiki", "batomon-wiki.db")), entries.Count > 0 && !correct);
    }

    public void Install(string directory, string? loaderDirectory = null)
    {
        ValidateTarget(directory);
        var readiness = Inspect(directory);
        if (readiness.ConfigurationConflict) throw new InvalidOperationException(readiness.Summary);
        var changes = new Dictionary<string, byte[]>();
        foreach (var name in LoaderFiles)
        {
            var target = Path.Combine(directory, "Mods", name);
            if (File.Exists(target)) continue;
            var source = loaderDirectory is null ? "" : Path.Combine(loaderDirectory, "Mods", name);
            if (!File.Exists(source) && loaderDirectory is not null) source = Path.Combine(loaderDirectory, name);
            if (!File.Exists(source)) throw new InvalidOperationException("Download and extract Mod Loader 1.2.2, then select its folder. Missing: " + name);
            changes[target] = File.ReadAllBytes(source);
        }
        foreach (var name in BridgeFiles)
            changes[Path.Combine(directory, "Mods", "BatomonHelperBridge", name)] = File.ReadAllBytes(Path.Combine(_bundle, "mods", "BatomonHelperBridge", name));
        var configPath = Path.Combine(directory, "override.cfg");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        if (!Regex.IsMatch(config, @"(?m)^\s*ModLoader\s*="))
        {
            var section = new Regex(@"(?m)^\s*\[autoload\][ \t]*\r?$");
            config = section.IsMatch(config) ? section.Replace(config, "[autoload]\r\n" + Autoload, 1)
                : config + "\r\n[autoload]\r\n" + Autoload + "\r\n";
            changes[configPath] = System.Text.Encoding.UTF8.GetBytes(config);
        }
        Apply(directory, changes.ToDictionary(pair => pair.Key, pair => (byte[]?)pair.Value));
    }

    public void Uninstall(string directory)
    {
        ValidateTarget(directory);
        Apply(directory, BridgeFiles.ToDictionary(name => Path.Combine(directory, "Mods", "BatomonHelperBridge", name), _ => (byte[]?)null));
    }

    private void ValidateTarget(string directory)
    {
        if (!IsGameDirectory(directory)) throw new InvalidOperationException("Select the folder containing batomon_showdown.exe and batomon_showdown.pck.");
        if (_running()) throw new InvalidOperationException("Close Batomon Showdown before changing mods.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Mod installation cannot write through a linked directory: " + current.FullName);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Mod installation cannot replace a linked file: " + path);
    }

    private void Apply(string directory, Dictionary<string, byte[]?> changes)
    {
        foreach (var path in changes.Keys) RejectLinks(path);
        var originals = changes.Keys.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        // Stage every payload before touching the game; retain backups of replaced files.
        var stage = Path.Combine(Path.GetTempPath(), "BatomonHelper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var touched = new List<string>();
        try
        {
            var index = 0;
            foreach (var bytes in changes.Values) if (bytes is not null) File.WriteAllBytes(Path.Combine(stage, (index++).ToString()), bytes);
            ValidateTarget(directory);
            var backupRoot = Path.Combine(directory, ".batomon-helper-backups", Guid.NewGuid().ToString("N"));
            foreach (var (path, bytes) in originals.Where(pair => pair.Value is not null))
            {
                var backup = Path.Combine(backupRoot, Path.GetRelativePath(directory, path));
                RejectLinks(backup);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.WriteAllBytes(backup, bytes!);
            }
            ValidateTarget(directory);
            foreach (var (path, bytes) in changes)
            {
                ValidateTarget(directory);
                RejectLinks(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                touched.Add(path);
                if (bytes is null) File.Delete(path);
                else _write(path, bytes);
            }
        }
        catch
        {
            foreach (var path in touched.AsEnumerable().Reverse())
            {
                if (originals[path] is { } bytes) File.WriteAllBytes(path, bytes);
                else File.Delete(path);
            }
            throw;
        }
        finally { Directory.Delete(stage, true); }
    }

    public static IEnumerable<string> FindSteamGames(IEnumerable<string> steamRoots)
    {
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraries = new List<string> { root };
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
                libraries.AddRange(Regex.Matches(File.ReadAllText(libraryFile), "\"path\"\\s*\"([^\"]+)\"")
                    .Select(match => match.Groups[1].Value.Replace("\\\\", "\\")));
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
                if (!File.Exists(manifest)) continue;
                var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
                if (!match.Success) continue;
                var common = Path.GetFullPath(Path.Combine(library, "steamapps", "common")) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(Path.Combine(common, match.Groups[1].Value));
                if (candidate.StartsWith(common, StringComparison.OrdinalIgnoreCase) && IsGameDirectory(candidate)) yield return candidate;
            }
        }
    }
}

public sealed record HelperSettings(
    string? GameDirectory = null,
    bool ShowDpsCounters = true,
    bool ShowBestDpsBoardSuggestions = true,
    bool ShowBattleOutcome = true,
    bool AutoInstallUpdates = false)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper", "settings.json");
    public static HelperSettings Load()
    {
        try { return JsonSerializer.Deserialize<HelperSettings>(File.ReadAllText(DefaultPath)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this));
    }
}
