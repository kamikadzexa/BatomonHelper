using BatomonHelper.Core;

namespace BatomonHelper.Tests;

public sealed class GameInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BatomonHelperTests-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "game");
    private string Bundle => Path.Combine(_root, "bundle");
    private string Loader => Path.Combine(_root, "loader");
    public GameInstallationTests()
    {
        Put(Game, "batomon_showdown.exe", "test"); Put(Game, "batomon_showdown.pck", "test");
        Put(Bundle, "mods/BatomonHelperBridge/mod.json", "{\"version\":\"1.4.0\"}");
        Put(Bundle, "mods/BatomonHelperBridge/mod_main.gd", "bridge");
        Put(Bundle, "assets/wiki/batomon-wiki.db", "catalog");
        foreach (var name in GameInstallation.LoaderFiles) Put(Loader, "Mods/" + name, "loader");
    }
    [Fact]
    public void Install_repair_and_uninstall_preserve_other_mods_and_configuration()
    {
        Put(Game, "override.cfg", "[display]\nfoo=1\n[autoload]\nOther=\"*other.gd\"\n");
        Put(Game, "Mods/Other/mod_main.gd", "other");
        var service = new GameInstallation(Bundle, () => false);
        Assert.False(service.Inspect(Game).LoaderReady);
        service.Install(Game, Loader);
        Assert.True(service.Inspect(Game).CanLaunch);
        Assert.Contains("Other=", File.ReadAllText(Path.Combine(Game, "override.cfg")));
        Assert.Contains("foo=1", File.ReadAllText(Path.Combine(Game, "override.cfg")));
        Put(Game, "Mods/BatomonHelperBridge/mod_main.gd", "outdated");
        Assert.False(service.Inspect(Game).BridgeCurrent);
        service.Install(Game);
        Assert.True(service.Inspect(Game).BridgeCurrent);
        Put(Game, "Mods/BatomonHelperBridge/custom.txt", "preserve");
        service.Uninstall(Game);
        Assert.False(service.Inspect(Game).BridgeInstalled);
        Assert.True(service.Inspect(Game).LoaderReady);
        Assert.True(File.Exists(Path.Combine(Game, "Mods/Other/mod_main.gd")));
        Assert.True(File.Exists(Path.Combine(Game, "Mods/BatomonHelperBridge/custom.txt")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Game, ".batomon-helper-backups"), "*", SearchOption.AllDirectories));
    }
    [Fact]
    public void Invalid_loader_or_conflicting_autoload_never_writes_game_files()
    {
        var service = new GameInstallation(Bundle, () => false);
        File.Delete(Path.Combine(Loader, "Mods/mod_title_state.gd"));
        Assert.Throws<InvalidOperationException>(() => service.Install(Game, Loader));
        Assert.False(Directory.Exists(Path.Combine(Game, "Mods")));
        Put(Game, "override.cfg", "[autoload]\nModLoader=\"*custom.gd\"\n");
        Assert.True(service.Inspect(Game).ConfigurationConflict);
        Assert.Throws<InvalidOperationException>(() => service.Install(Game, Loader));
        Assert.Equal("[autoload]\nModLoader=\"*custom.gd\"\n", File.ReadAllText(Path.Combine(Game, "override.cfg")));
    }
    [Fact]
    public void Running_game_blocks_changes_including_a_game_started_during_staging()
    {
        var running = true;
        var service = new GameInstallation(Bundle, () => running);
        Assert.Throws<InvalidOperationException>(() => service.Install(Game, Loader));
        Assert.Throws<InvalidOperationException>(() => service.Uninstall(Game));
        running = false;
        var checks = 0;
        service = new GameInstallation(Bundle, () => ++checks >= 3);
        Assert.Throws<InvalidOperationException>(() => service.Install(Game, Loader));
        Assert.False(Directory.Exists(Path.Combine(Game, "Mods")));
    }
    [Fact]
    public void Failed_write_rolls_back_previous_files_and_reports_access_error()
    {
        Put(Game, "Mods/mod_loader.gd", "existing-loader");
        Put(Game, "override.cfg", "[display]\nfoo=1\n");
        var writes = 0;
        var service = new GameInstallation(Bundle, () => false, (path, bytes) =>
        {
            if (++writes == 3) throw new UnauthorizedAccessException("test denied");
            File.WriteAllBytes(path, bytes);
        });
        Assert.Throws<UnauthorizedAccessException>(() => service.Install(Game, Loader));
        Assert.Equal("existing-loader", File.ReadAllText(Path.Combine(Game, "Mods/mod_loader.gd")));
        Assert.Equal("[display]\nfoo=1\n", File.ReadAllText(Path.Combine(Game, "override.cfg")));
        Assert.False(File.Exists(Path.Combine(Game, "Mods/mod_settings_menu.gd")));
        Assert.False(File.Exists(Path.Combine(Game, "Mods/BatomonHelperBridge/mod.json")));
    }
    [Fact]
    public void Readiness_distinguishes_missing_game_bridge_catalog_and_running_game()
    {
        var running = false;
        var service = new GameInstallation(Bundle, () => running);
        Assert.False(service.Inspect(null).GameFound);
        service.Install(Game, Loader);
        running = true;
        Assert.True(service.Inspect(Game).Running);
        Assert.False(service.Inspect(Game).CanLaunch);
        File.Delete(Path.Combine(Bundle, "assets/wiki/batomon-wiki.db"));
        Assert.False(service.Inspect(Game).CatalogReady);
        Put(Game, "Mods/BatomonHelperBridge/mod.json", "old");
        Assert.False(service.Inspect(Game).BridgeCurrent);
    }
    [Fact]
    public void Steam_discovery_reads_secondary_library_manifest_and_validates_files()
    {
        var steam = Path.Combine(_root, "steam");
        var library = Path.Combine(_root, "secondary");
        Put(steam, "steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" } }");
        Put(library, "steamapps/appmanifest_4557380.acf", "\"AppState\" { \"installdir\" \"Batomon Showdown\" }");
        Put(library, "steamapps/common/Batomon Showdown/batomon_showdown.exe", "test");
        Assert.Empty(GameInstallation.FindSteamGames([steam]));
        Put(library, "steamapps/common/Batomon Showdown/batomon_showdown.pck", "test");
        Assert.Single(GameInstallation.FindSteamGames([steam]));
    }
    [Theory]
    [InlineData("[display]\nModLoader=\"*res://Mods/mod_loader.gd\"\n")]
    [InlineData("[autoload]\nModLoader=\"*res://Mods/mod_loader.gd\"\nModLoader=\"*res://Mods/mod_loader.gd\"\n")]
    public void Duplicate_or_misplaced_loader_entries_are_conflicts(string config)
    {
        Put(Game, "override.cfg", config);
        var service = new GameInstallation(Bundle, () => false);
        Assert.True(service.Inspect(Game).ConfigurationConflict);
        Assert.Throws<InvalidOperationException>(() => service.Install(Game, Loader));
        Assert.Equal(config, File.ReadAllText(Path.Combine(Game, "override.cfg")));
    }
    [Fact]
    public void Downloaded_loader_selection_accepts_extracted_layouts_and_rejects_partial_downloads()
    {
        Assert.False(GameInstallation.IsLoaderDirectory(null));
        Assert.False(GameInstallation.IsLoaderDirectory(Game));
        Assert.True(GameInstallation.IsLoaderDirectory(Loader));
        Assert.True(GameInstallation.IsLoaderDirectory(Path.Combine(Loader, "Mods")));
        File.Delete(Path.Combine(Loader, "Mods", "mod_title_state.gd"));
        Assert.False(GameInstallation.IsLoaderDirectory(Loader));
    }
    private static void Put(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
