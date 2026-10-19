using BatomonHelper.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace BatomonHelper.Overlay;

public sealed class GameSetupViewModel : INotifyPropertyChanged
{
    private readonly GameInstallation _installation = new(AppContext.BaseDirectory);
    private readonly GitHubReleaseUpdater _updater = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _directory;
    private string? _loaderDirectory;
    private HelperSettings _settings;
    private string _message = "";
    private bool _busy;
    private bool _connected;
    private string _connection = "Waiting for game connection.";
    private GameReadiness _readiness = new(false, false, false, false, false, false, false);
    private readonly HashSet<string> _steamGames = new(StringComparer.OrdinalIgnoreCase);
    private ReleaseUpdate? _availableUpdate;
    private string _updateStatus = "Checking for helper updates…";
    public event PropertyChangedEventHandler? PropertyChanged;

    public GameSetupViewModel()
    {
        _settings = HelperSettings.Load();
        _directory = _settings.GameDirectory;
        try
        {
            var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") };
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string root) roots.Add(root);
            foreach (var game in GameInstallation.FindSteamGames(roots)) _steamGames.Add(game);
            _directory ??= _steamGames.FirstOrDefault();
        }
        catch (Exception error) { _message = "Steam discovery failed. Browse to the game folder. " + error.Message; }
        BrowseCommand = new RelayCommand(Browse);
        DownloadLoaderCommand = new RelayCommand(() => Open(GameInstallation.LoaderDownload));
        SelectLoaderCommand = new RelayCommand(SelectLoader);
        InstallCommand = new AsyncCommand(() => ChangeModAsync(false));
        UninstallCommand = new AsyncCommand(() => ChangeModAsync(true));
        LaunchCommand = new RelayCommand(Launch);
        RefreshCommand = new RelayCommand(Refresh);
        CheckForUpdatesCommand = new AsyncCommand(CheckForUpdatesAsync);
        InstallUpdateCommand = new AsyncCommand(InstallUpdateAsync);
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _ = CheckForUpdatesAsync();
    }

    public string GameDirectory => _directory ?? "No game folder selected";
    public string LoaderDirectory => _loaderDirectory ?? "No downloaded loader selected.";
    public bool LoaderInstalled => _readiness.LoaderReady;
    public bool NeedsSetup => !_readiness.GameFound || !_readiness.Running || !_readiness.LoaderReady || !_readiness.BridgeCurrent;
    private bool MissingLoaderFiles => !_readiness.GameFound || GameInstallation.LoaderFiles.Any(name => !File.Exists(Path.Combine(_directory!, "Mods", name)));
    public string Summary => _readiness.Running && _connected ? "Game connected. Live bridge is ready." : _readiness.Summary;
    public string Checks => $"Game files: {(_readiness.GameFound ? "found" : "missing")}   •   Game: {(_readiness.Running ? "running" : "not running")}\n"
        + $"Mod Loader: {(_readiness.LoaderReady ? "ready" : "setup required")}   •   Helper bridge: {(_readiness.BridgeCurrent ? "current" : _readiness.BridgeInstalled ? "update/repair required" : "missing")}\n"
        + $"Bundled catalog: {(_readiness.CatalogReady ? "found" : "missing")}";
    public string Message => _message;
    public string UpdateStatus => _updateStatus;
    public bool UpdateAvailable => _availableUpdate is not null;
    public bool CanInstallUpdate => !_busy && _availableUpdate is not null;
    public bool AutoInstallUpdates
    {
        get => _settings.AutoInstallUpdates;
        set => SaveOptions(_settings with { AutoInstallUpdates = value });
    }
    public string Connection => _connection;
    public bool ShowDpsCounters
    {
        get => _settings.ShowDpsCounters;
        set => SaveOptions(_settings with { ShowDpsCounters = value });
    }
    public bool ShowBestDpsBoardSuggestions
    {
        get => _settings.ShowBestDpsBoardSuggestions;
        set => SaveOptions(_settings with { ShowBestDpsBoardSuggestions = value });
    }
    public bool ShowBattleOutcome
    {
        get => _settings.ShowBattleOutcome;
        set => SaveOptions(_settings with { ShowBattleOutcome = value });
    }
    public void SetConnection(string message)
    {
        _connected = string.IsNullOrWhiteSpace(message);
        _connection = _connected ? "Bridge connected; live heartbeat accepted." : message;
        NotifyAll();
    }
    public bool CanInstall => !_busy && _readiness.GameFound && !_readiness.Running && !_readiness.ConfigurationConflict
        && (!MissingLoaderFiles || GameInstallation.IsLoaderDirectory(_loaderDirectory));
    public bool CanUninstall => !_busy && _readiness.GameFound && !_readiness.Running && Directory.Exists(Path.Combine(_directory!, "Mods", "BatomonHelperBridge"));
    public bool CanLaunch => !_busy && _readiness.CanLaunch;
    public bool CanBrowse => !_busy;
    public string InstallLabel => _readiness.BridgeInstalled ? "Update/repair mod" : "Install mod";
    public ICommand BrowseCommand { get; }
    public ICommand DownloadLoaderCommand { get; }
    public ICommand SelectLoaderCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        if (_busy) return;
        try { _readiness = _installation.Inspect(_directory); }
        catch (Exception error)
        {
            _readiness = new(false, false, false, false, false, false, false);
            _message = "Could not inspect game setup: " + error.Message;
        }
        NotifyAll();
    }

    private void Browse()
    {
        var picker = new OpenFolderDialog { Title = "Select the Batomon Showdown game folder" };
        if (picker.ShowDialog() != true) return;
        if (!GameInstallation.IsGameDirectory(picker.FolderName)) { _message = "Choose a folder containing batomon_showdown.exe and batomon_showdown.pck."; NotifyAll(); return; }
        _directory = picker.FolderName;
        try { SaveOptions(_settings with { GameDirectory = _directory }); _message = "Game folder saved."; }
        catch (Exception error) { _message = "Could not save game folder: " + error.Message; }
        Refresh();
    }

    private async Task ChangeModAsync(bool uninstall)
    {
        Refresh();
        if (uninstall ? !CanUninstall : !CanInstall) return;
        var loader = !uninstall && MissingLoaderFiles ? _loaderDirectory : null;
        _busy = true;
        NotifyAll();
        try
        {
            try
            {
                await Task.Run(() => { if (uninstall) _installation.Uninstall(_directory!); else _installation.Install(_directory!, loader); });
            }
            catch (UnauthorizedAccessException)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                info.ArgumentList.Add(uninstall ? "--uninstall-bridge" : "--install-bridge");
                info.ArgumentList.Add(_directory!);
                if (loader is not null) { info.ArgumentList.Add("--loader"); info.ArgumentList.Add(loader); }
                using var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start elevated setup.");
                await child.WaitForExitAsync();
                if (child.ExitCode != 0) throw new InvalidOperationException("Elevated mod setup failed. Check the selected folder, close the game, and try again.");
            }
            _message = uninstall ? "Helper bridge removed. The shared Mod Loader and other mods were retained."
                : "Helper bridge installed. Launch the game to connect. Backups are in the game's .batomon-helper-backups folder.";
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { _message = "Permission request cancelled. Mod setup was not completed."; }
        catch (Exception error) { _message = "Mod setup failed: " + error.Message; }
        finally { _busy = false; Refresh(); }
    }

    private void SelectLoader()
    {
        if (_busy) return;
        var picker = new OpenFolderDialog { Title = "Select the downloaded and extracted Mod Loader folder" };
        if (picker.ShowDialog() != true) return;
        if (!GameInstallation.IsLoaderDirectory(picker.FolderName))
        {
            _message = "Select an extracted Mod Loader folder containing all three loader scripts, either directly or inside Mods.";
            NotifyAll();
            return;
        }
        _loaderDirectory = picker.FolderName;
        _message = "Downloaded loader selected. Close the game, then choose Install mod to install the loader and bridge.";
        Refresh();
    }

    private void Launch()
    {
        Refresh();
        if (!CanLaunch) return;
        Open(_steamGames.Contains(_directory!) ? $"steam://rungameid/{GameInstallation.SteamAppId}" : Path.Combine(_directory!, "batomon_showdown.exe"));
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_busy) return;
        _updateStatus = "Checking GitHub releases…";
        NotifyAll();
        try
        {
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            _availableUpdate = await _updater.FindLatestAsync(new Version(current.Major, current.Minor, Math.Max(0, current.Build)));
            _updateStatus = _availableUpdate is null
                ? $"You have the latest version ({GitHubReleaseUpdater.FormatVersion(current)})."
                : $"Version {GitHubReleaseUpdater.FormatVersion(_availableUpdate.Version)} is available from GitHub Releases.";
            NotifyAll();
            if (_availableUpdate is not null && AutoInstallUpdates) await InstallUpdateAsync();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            _availableUpdate = null;
            _updateStatus = "Could not check GitHub Releases. You can try again later.";
            NotifyAll();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_busy || _availableUpdate is null) return;
        _busy = true;
        NotifyAll();
        try
        {
            _updateStatus = $"Downloading verified installer for {GitHubReleaseUpdater.FormatVersion(_availableUpdate.Version)}…";
            NotifyAll();
            var installer = await _updater.DownloadVerifiedInstallerAsync(_availableUpdate);
            _updateStatus = "Starting installer…";
            NotifyAll();
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            _updateStatus = "Update could not be installed: " + error.Message;
            _busy = false;
            NotifyAll();
        }
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = _directory is not null && Directory.Exists(_directory) ? _directory : AppContext.BaseDirectory }); }
        catch (Exception error) { _message = "Could not open: " + error.Message; NotifyAll(); }
    }
    private void SaveOptions(HelperSettings settings)
    {
        if (_settings == settings) return;
        _settings = settings;
        try { _settings.Save(); }
        catch (Exception error) { _message = "Could not save helper options: " + error.Message; }
        NotifyAll();
    }
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
