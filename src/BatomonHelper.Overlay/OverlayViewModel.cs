using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BatomonHelper.Overlay;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly SnapshotRepository _repository;
    private readonly WikiImporter _wikiImporter;
    private readonly IShopScreenReader _screenReader = new WindowsShopScreenReader();
    private readonly CaptureHistoryStore _history;
    private readonly IOddsCalculator _oddsCalculator = new OddsCalculator();
    private CatalogSnapshot _catalog = CatalogSnapshot.Empty;
    private bool _isPinned;
    private string _status = "Capture a focused Batomon shop with F9 or Capture Shop.";
    private string _warning = "";
    private string _provenance = "No catalog loaded.";
    private string _runSummary = "No shop has been captured.";
    private string _notes = "";

    public OverlayViewModel()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper");
        Directory.CreateDirectory(appData);
        _repository = new SnapshotRepository(Path.Combine(appData, "batomon-helper.db"));
        _history = new CaptureHistoryStore(Path.Combine(appData, "capture-history"));
        _wikiImporter = new WikiImporter(new BatomonWikiSource(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        RefreshWikiCommand = new AsyncCommand(RefreshWikiAsync);
        CaptureShopCommand = new AsyncCommand(CaptureShopAsync);
        TogglePinnedCommand = new RelayCommand(TogglePinned);
        _ = InitializeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RarityRow> RarityRows { get; } = [];
    public ObservableCollection<CreatureRow> EligibleCreatureRows { get; } = [];
    public IReadOnlyList<RegionFilterOption> RegionFilters { get; } = [new(MonsterRegionFilter.All, "All regions"), new(MonsterRegionFilter.Jinto, "Jinto region"), new(MonsterRegionFilter.Pantra, "Pantra region")];
    public ICommand RefreshWikiCommand { get; }
    public ICommand CaptureShopCommand { get; }
    public ICommand TogglePinnedCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Warning { get => _warning; private set { Set(ref _warning, value); OnPropertyChanged(nameof(WarningVisibility)); } }
    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(Warning) ? Visibility.Collapsed : Visibility.Visible;
    public string Provenance { get => _provenance; private set => Set(ref _provenance, value); }
    public string RunSummary { get => _runSummary; private set => Set(ref _runSummary, value); }
    public string Notes { get => _notes; private set => Set(ref _notes, value); }
    private RegionFilterOption _selectedRegionFilter = new(MonsterRegionFilter.All, "All regions");
    public RegionFilterOption SelectedRegionFilter { get => _selectedRegionFilter; set => Set(ref _selectedRegionFilter, value); }

    private async Task InitializeAsync()
    {
        await _repository.InitializeAsync();
        _catalog = await _repository.GetLatestAsync() ?? CatalogSnapshot.Empty;
        UpdateProvenance();
        if (_catalog == CatalogSnapshot.Empty || _catalog.Trainers.IsDefaultOrEmpty || _catalog.Items.IsDefaultOrEmpty || _catalog.Monsters.Any(monster => monster.Regions.IsEmpty) || DateTimeOffset.UtcNow - _catalog.RetrievedAt > TimeSpan.FromDays(1)) await RefreshWikiAsync();
    }

    private async Task RefreshWikiAsync()
    {
        Status = "Refreshing public wiki data…";
        var result = await _wikiImporter.ImportAsync(CancellationToken.None);
        if (result.Snapshot is null)
        {
            Warning = string.Join(" ", result.Errors);
            Status = "Using cached catalog. Capture remains available for manual review.";
            return;
        }
        _catalog = result.Snapshot;
        await _repository.SaveAsync(_catalog);
        UpdateProvenance();
        Status = "Wiki catalog refreshed. Capture a focused shop with F9.";
    }

    public async Task CaptureShopAsync()
    {
        var window = Application.Current.MainWindow;
        var restoreOpacity = window is not null && window.Visibility == Visibility.Visible ? window.Opacity : 1d;
        try
        {
            if (_catalog == CatalogSnapshot.Empty || _catalog.Trainers.IsDefaultOrEmpty || _catalog.Items.IsDefaultOrEmpty)
            {
                await RefreshWikiAsync();
                if (_catalog == CatalogSnapshot.Empty || _catalog.Trainers.IsDefaultOrEmpty || _catalog.Items.IsDefaultOrEmpty)
                {
                    Status = "The wiki catalog is required before a shop capture.";
                    return;
                }
            }
            if (window is not null && window.Visibility == Visibility.Visible) { window.Opacity = 0; await Task.Delay(90); }
            Status = "Capturing the focused Batomon shop…";
            var result = await _screenReader.CaptureAsync(_catalog);
            if (window is not null) window.Opacity = restoreOpacity;

            if (result.Snapshot is null)
            {
                await SaveHistoryAsync(result, false);
                Warning = string.Join(" ", result.Diagnostics.Select(diagnostic => diagnostic.Message));
                Status = "Shop capture was not accepted; the previous state is unchanged.";
                return;
            }

            result = result with
            {
                Snapshot = result.Snapshot with
                {
                    RegionFilter = SelectedRegionFilter.Value
                }
            };

            var review = new CaptureReviewWindow(result, _catalog) { Owner = window };
            var accepted = review.ShowDialog() == true && review.AcceptedSnapshot is not null;
            if (!accepted)
            {
                await SaveHistoryAsync(result, false);
                Status = "Shop capture was cancelled; the previous state is unchanged.";
                return;
            }

            var snapshot = review.AcceptedSnapshot!;
            var validation = ShopOddsSnapshotValidator.Validate(snapshot, _catalog);
            if (!validation.IsValid)
            {
                await SaveHistoryAsync(result, false);
                Warning = string.Join(" ", validation.Errors);
                Status = "Correct the highlighted capture values and try again; the previous state is unchanged.";
                return;
            }

            await SaveHistoryAsync(result with { Snapshot = snapshot }, true);
            Render(_oddsCalculator.Calculate(_catalog, snapshot), snapshot);
            Warning = result.Fields.Any(field => !field.IsResolved) ? "Some OCR fields were manually reviewed or remain low confidence." : "";
            Status = $"Shop captured at {snapshot.CapturedAt:HH:mm:ss}. Press F9 after your next shop change.";
        }
        catch (Exception exception)
        {
            Warning = exception.Message;
            Status = "Shop capture failed; the previous state is unchanged.";
        }
        finally
        {
            if (window is not null) window.Opacity = restoreOpacity;
        }
    }

    private async Task SaveHistoryAsync(ShopCaptureResult result, bool accepted)
    {
        try { await _history.SaveAsync(result, accepted); }
        catch (Exception exception) { Warning = $"Capture history could not be saved: {exception.Message}"; }
    }

    private void Render(OddsResult odds, ShopOddsSnapshot snapshot)
    {
        RarityRows.Clear();
        foreach (var item in odds.RarityOdds) RarityRows.Add(new(item.Rarity.ToString(), item.Probability.ToString("P2"), item.EligibleCount, item.RolledAs.ToString()));
        EligibleCreatureRows.Clear();
        foreach (var monster in odds.MonsterOdds) EligibleCreatureRows.Add(new(monster.Name, monster.Rarity.ToString(), monster.PerBatomonRoll.ToString("P2"), monster.NextShopChance.ToString("P2")));
        RunSummary = $"{snapshot.Trainer ?? "Unknown trainer"} · Rank {snapshot.ShopRank} · ${snapshot.Gold} · item {snapshot.ItemUsesRemaining}/{snapshot.DailyItemLimit}: {snapshot.ItemOffer?.Name ?? "none"} · {snapshot.RegionFilter} · {odds.BatomonSlotsNextShop} Batomon slot(s) next shop";
        Notes = string.Join(" ", new[] { snapshot.ItemOffer?.RuleText }.Concat(odds.Notes).Where(note => !string.IsNullOrWhiteSpace(note)));
    }

    private void UpdateProvenance() => Provenance = $"Wiki build {_catalog.DeclaredSteamBuild} · retrieved {_catalog.RetrievedAt:yyyy-MM-dd HH:mm} UTC · source: manual shop OCR";

    private void TogglePinned()
    {
        _isPinned = !_isPinned;
        if (Application.Current.MainWindow is { } window) NativeOverlay.SetClickThrough(window, _isPinned);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record RarityRow(string Name, string Chance, int Eligible, string Fallback);
public sealed record CreatureRow(string Name, string Rarity, string PerRoll, string NextShop);
public sealed record RegionFilterOption(MonsterRegionFilter Value, string Name);

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
