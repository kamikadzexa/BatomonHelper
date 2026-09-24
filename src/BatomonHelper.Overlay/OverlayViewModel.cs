using BatomonHelper.Core;
using BatomonHelper.GameReader;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BatomonHelper.Overlay;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly SnapshotRepository _repository;
    private readonly WikiImporter _wikiImporter;
    private readonly IGameStateReader _reader = new ProfiledGameStateReader();
    private readonly IOddsCalculator _oddsCalculator = new OddsCalculator();
    private readonly DispatcherTimer _timer;
    private CatalogSnapshot _catalog = CatalogSnapshot.Empty;
    private MemoryProfile? _profile;
    private SteamInstall? _install;
    private bool _isPinned;
    private string _status = "Starting…";
    private string _warning = "";
    private string _provenance = "No catalog loaded.";
    private string _runSummary = "Waiting for a game state.";
    private string _notes = "";

    public OverlayViewModel()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper");
        Directory.CreateDirectory(appData);
        _repository = new SnapshotRepository(Path.Combine(appData, "batomon-helper.db"));
        _wikiImporter = new WikiImporter(new BatomonWikiSource(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        RefreshWikiCommand = new AsyncCommand(RefreshWikiAsync);
        TogglePinnedCommand = new RelayCommand(TogglePinned);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _timer.Tick += async (_, _) => await RefreshStateAsync();
        _ = InitializeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RarityRow> RarityRows { get; } = [];
    public ICommand RefreshWikiCommand { get; }
    public ICommand TogglePinnedCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Warning { get => _warning; private set { Set(ref _warning, value); OnPropertyChanged(nameof(WarningVisibility)); } }
    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(Warning) ? Visibility.Collapsed : Visibility.Visible;
    public string Provenance { get => _provenance; private set => Set(ref _provenance, value); }
    public string RunSummary { get => _runSummary; private set => Set(ref _runSummary, value); }
    public string Notes { get => _notes; private set => Set(ref _notes, value); }

    private async Task InitializeAsync()
    {
        await _repository.InitializeAsync();
        _catalog = await _repository.GetLatestAsync() ?? CatalogSnapshot.Empty;
        _install = await SteamBuildDetector.DiscoverAsync();
        var profiles = await new MemoryProfileStore(Path.Combine(AppContext.BaseDirectory, "profiles")).LoadAsync();
        _profile = profiles.FirstOrDefault(p => _install is not null && p.SteamBuildId == _install.BuildId && p.ExecutableSha256.Equals(_install.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(p => p.IsComplete);
        UpdateProvenance();
        if (_catalog == CatalogSnapshot.Empty || DateTimeOffset.UtcNow - _catalog.RetrievedAt > TimeSpan.FromDays(1)) await RefreshWikiAsync();
        _timer.Start();
    }

    private async Task RefreshWikiAsync()
    {
        Status = "Refreshing public wiki data…";
        var result = await _wikiImporter.ImportAsync(CancellationToken.None);
        if (result.Snapshot is null)
        {
            Warning = string.Join(" ", result.Errors);
            Status = "Using cached catalog.";
            return;
        }
        _catalog = result.Snapshot;
        await _repository.SaveAsync(_catalog);
        UpdateProvenance();
        Status = "Wiki catalog refreshed.";
    }

    private async Task RefreshStateAsync()
    {
        var install = _install ?? await SteamBuildDetector.DiscoverAsync();
        if (install is not null) _install = install;
        var verified = _profile is not null && _install is not null && _profile.SteamBuildId == _install.BuildId && _profile.ExecutableSha256.Equals(_install.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
        var result = await _reader.ReadAsync(_profile, allowStaleProfile: !verified, CancellationToken.None);
        Status = result.Status;
        Warning = result.IsUnverified ? $"UNVERIFIED BUILD: detected {_install?.BuildId ?? "unknown"}; profile {_profile?.SteamBuildId ?? "none"}. Odds are calculated with warnings and may be wrong." : string.Join(" ", result.Errors);
        if (result.State is null) return;
        var validation = RunStateValidator.Validate(result.State, _catalog);
        if (!validation.IsValid) { Warning = string.Join(" ", validation.Errors); return; }
        Render(_oddsCalculator.Calculate(_catalog, result.State), result.State);
    }

    private void Render(OddsResult odds, RunState state)
    {
        RarityRows.Clear();
        foreach (var item in odds.RarityOdds) RarityRows.Add(new(item.Rarity.ToString(), item.Probability.ToString("P2"), item.EligibleCount, item.RolledAs.ToString()));
        RunSummary = $"Day {state.Day} · Rank {state.ShopRank} · ${state.Gold} · {odds.BatomonSlotsNextShop} Batomon slot(s) next fresh shop";
        Notes = string.Join(" ", odds.Notes);
    }

    private void UpdateProvenance()
    {
        Provenance = $"Wiki build {_catalog.DeclaredSteamBuild} · retrieved {_catalog.RetrievedAt:yyyy-MM-dd HH:mm} UTC · game build {_install?.BuildId ?? "not found"}";
        if (_catalog.DeclaredSteamBuild != "unknown" && _install is not null && _catalog.DeclaredSteamBuild != _install.BuildId)
            Warning = $"DATA BUILD MISMATCH: wiki {_catalog.DeclaredSteamBuild}; installed game {_install.BuildId}.";
    }

    private void TogglePinned()
    {
        _isPinned = !_isPinned;
        if (Application.Current.MainWindow is { } window) NativeOverlay.SetClickThrough(window, _isPinned);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record RarityRow(string Name, string Chance, int Eligible, string Fallback);

public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

public sealed class AsyncCommand(Func<Task> action) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await action(); } finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
