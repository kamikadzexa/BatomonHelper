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
    private readonly IShopScreenReader _screenReader = new WindowsShopScreenReader();
    private readonly CaptureHistoryStore _history;
    private readonly IOddsCalculator _oddsCalculator = new OddsCalculator();
    private CatalogSnapshot _catalog = CatalogSnapshot.Empty;
    private IReadOnlyDictionary<string, MonsterOdds> _oddsByMonster = new Dictionary<string, MonsterOdds>();
    private bool _isPinned;
    private string _searchText = string.Empty;
    private string _status = "Loading bundled creature catalog…";
    private string _warning = string.Empty;
    private string _runSummary = "No shop has been captured.";
    private string _notes = string.Empty;
    private RegionFilterOption _selectedRegionFilter = new(MonsterRegionFilter.All, "All regions");
    private RarityFilterOption _selectedRarityFilter = new(null, "All rarities");

    public OverlayViewModel()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper");
        Directory.CreateDirectory(appData);
        _history = new CaptureHistoryStore(Path.Combine(appData, "capture-history"));
        _repository = new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "assets", "wiki", "batomon-wiki.db"), readOnly: true);
        CaptureShopCommand = new AsyncCommand(CaptureShopAsync);
        TogglePinnedCommand = new RelayCommand(TogglePinned);
        _ = InitializeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CreatureBrowserRow> CreatureRows { get; } = [];
    public IReadOnlyList<RegionFilterOption> RegionFilters { get; } = [new(MonsterRegionFilter.All, "All regions"), new(MonsterRegionFilter.Jinto, "Jinto region"), new(MonsterRegionFilter.Pantra, "Pantra region")];
    public IReadOnlyList<RarityFilterOption> RarityFilters { get; } = [new(null, "All rarities"), new(Rarity.Common, "Common"), new(Rarity.Uncommon, "Uncommon"), new(Rarity.Rare, "Rare"), new(Rarity.SuperRare, "Super Rare"), new(Rarity.Legendary, "Legendary"), new(Rarity.Mythical, "Mythical")];
    public ICommand CaptureShopCommand { get; }
    public ICommand TogglePinnedCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Warning { get => _warning; private set { Set(ref _warning, value); OnPropertyChanged(nameof(WarningVisibility)); } }
    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(Warning) ? Visibility.Collapsed : Visibility.Visible;
    public string RunSummary { get => _runSummary; private set => Set(ref _runSummary, value); }
    public string Notes { get => _notes; private set => Set(ref _notes, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshCreatureRows(); } }
    public RegionFilterOption SelectedRegionFilter { get => _selectedRegionFilter; set { if (Set(ref _selectedRegionFilter, value)) RefreshCreatureRows(); } }
    public RarityFilterOption SelectedRarityFilter { get => _selectedRarityFilter; set { if (Set(ref _selectedRarityFilter, value)) RefreshCreatureRows(); } }
    public string CreatureCount => $"{CreatureRows.Count} creatures";

    private async Task InitializeAsync()
    {
        try
        {
            _catalog = await _repository.GetLatestAsync() ?? CatalogSnapshot.Empty;
            if (_catalog == CatalogSnapshot.Empty || _catalog.Monsters.IsEmpty)
            {
                Warning = "The bundled wiki catalog is missing. Reinstall the app build that includes assets/wiki/batomon-wiki.db.";
                Status = "Bundled catalog unavailable.";
                return;
            }
            RefreshCreatureRows();
            Status = $"Loaded {_catalog.Monsters.Length} bundled creatures. Press F9 to capture the focused shop.";
        }
        catch (Exception exception)
        {
            Warning = $"The bundled wiki catalog could not be opened: {exception.Message}";
            Status = "Bundled catalog unavailable.";
        }
    }

    public async Task CaptureShopAsync()
    {
        var window = Application.Current.MainWindow;
        var restoreOpacity = window is not null && window.Visibility == Visibility.Visible ? window.Opacity : 1d;
        try
        {
            if (_catalog == CatalogSnapshot.Empty || _catalog.Trainers.IsDefaultOrEmpty || _catalog.Items.IsDefaultOrEmpty)
            {
                Status = "The bundled catalog is unavailable; shop capture cannot start.";
                return;
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
            result = result with { Snapshot = result.Snapshot with { RegionFilter = SelectedRegionFilter.Value } };
            var review = new CaptureReviewWindow(result, _catalog) { Owner = window };
            if (review.ShowDialog() != true || review.AcceptedSnapshot is null)
            {
                await SaveHistoryAsync(result, false);
                Status = "Shop capture was cancelled; the previous state is unchanged.";
                return;
            }
            var snapshot = review.AcceptedSnapshot;
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
            Warning = result.Fields.Any(field => !field.IsResolved) ? "Some OCR fields were manually reviewed or remain low confidence." : string.Empty;
            Status = $"Shop captured at {snapshot.CapturedAt:HH:mm:ss}. Press F9 after your next shop change.";
        }
        catch (Exception exception)
        {
            Warning = exception.Message;
            Status = "Shop capture failed; the previous state is unchanged.";
        }
        finally { if (window is not null) window.Opacity = restoreOpacity; }
    }

    private async Task SaveHistoryAsync(ShopCaptureResult result, bool accepted)
    {
        try { await _history.SaveAsync(result, accepted); }
        catch (Exception exception) { Warning = $"Capture history could not be saved: {exception.Message}"; }
    }

    private void Render(OddsResult odds, ShopOddsSnapshot snapshot)
    {
        _oddsByMonster = odds.MonsterOdds.ToDictionary(monster => monster.MonsterId, StringComparer.OrdinalIgnoreCase);
        RefreshCreatureRows();
        RunSummary = $"{snapshot.Trainer ?? "Unknown trainer"} · Rank {snapshot.ShopRank} · ${snapshot.Gold} · item {snapshot.ItemUsesRemaining}/{snapshot.DailyItemLimit}: {snapshot.ItemOffer?.Name ?? "none"} · {snapshot.RegionFilter} · {odds.BatomonSlotsNextShop} Batomon slot(s) next shop";
        Notes = string.Join(" ", new[] { snapshot.ItemOffer?.RuleText }.Concat(odds.Notes).Where(note => !string.IsNullOrWhiteSpace(note)));
    }

    private void RefreshCreatureRows()
    {
        if (_catalog == CatalogSnapshot.Empty) return;
        var query = CreatureCatalogFilter.Apply(_catalog, SearchText, SelectedRegionFilter.Value, SelectedRarityFilter.Value);
        CreatureRows.Clear();
        foreach (var monster in query)
        {
            _oddsByMonster.TryGetValue(monster.Id, out var odds);
            var sprite = monster.SpritePath is null ? null : Path.Combine(AppContext.BaseDirectory, monster.SpritePath.Replace('/', Path.DirectorySeparatorChar));
            CreatureRows.Add(new(monster.Name, monster.Rarity == Rarity.SuperRare ? "Super Rare" : monster.Rarity.ToString(), string.Join(", ", monster.Regions.OrderBy(region => region)), odds?.PerBatomonRoll.ToString("P2") ?? "—", odds?.NextShopChance.ToString("P2") ?? "—", sprite));
        }
        OnPropertyChanged(nameof(CreatureCount));
    }

    private void TogglePinned()
    {
        _isPinned = !_isPinned;
        if (Application.Current.MainWindow is { } window) NativeOverlay.SetClickThrough(window, _isPinned);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record CreatureBrowserRow(string Name, string Rarity, string Regions, string PerRoll, string NextShop, string? SpritePath);
public sealed record RegionFilterOption(MonsterRegionFilter Value, string Name);
public sealed record RarityFilterOption(Rarity? Value, string Name);

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
