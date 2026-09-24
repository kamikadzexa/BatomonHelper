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
    private ShopOddsSnapshot? _snapshot;
    private ShopOddsSnapshot? _capturedSnapshot;
    private IReadOnlyDictionary<string, MonsterOdds> _oddsByMonster = new Dictionary<string, MonsterOdds>();
    private IReadOnlyDictionary<string, ItemOdds> _oddsByItem = new Dictionary<string, ItemOdds>();
    private string _searchText = string.Empty;
    private string _itemSearchText = string.Empty;
    private string _trinketSearchText = string.Empty;
    private string _status = "Loading bundled creature catalog…";
    private string _warning = string.Empty;
    private string _runSummary = "No shop has been captured.";
    private string _notes = string.Empty;
    private RegionFilterOption _selectedRegionFilter = new(MonsterRegionFilter.All, "All regions");
    private RarityFilterOption _selectedRarityFilter = new(null, "All rarities");
    private RarityFilterOption _selectedItemRarityFilter = new(null, "All rarities");
    private RarityFilterOption _selectedTrinketRarityFilter = new(null, "All rarities");

    public OverlayViewModel()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper");
        Directory.CreateDirectory(appData);
        _history = new CaptureHistoryStore(Path.Combine(appData, "capture-history"));
        _repository = new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "assets", "wiki", "batomon-wiki.db"), readOnly: true);
        CaptureShopCommand = new AsyncCommand(CaptureShopAsync);
        DecreaseRankCommand = new RelayCommand(() => ChangeRank(-1));
        IncreaseRankCommand = new RelayCommand(() => ChangeRank(1));
        _ = InitializeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CreatureBrowserRow> CreatureRows { get; } = [];
    public ObservableCollection<ItemBrowserRow> ItemRows { get; } = [];
    public ObservableCollection<TrinketBrowserRow> TrinketRows { get; } = [];
    public IReadOnlyList<RegionFilterOption> RegionFilters { get; } = [new(MonsterRegionFilter.All, "All regions"), new(MonsterRegionFilter.Jinto, "Jinto region"), new(MonsterRegionFilter.Pantra, "Pantra region")];
    public IReadOnlyList<RarityFilterOption> RarityFilters { get; } = [new(null, "All rarities"), new(Rarity.Common, "Common"), new(Rarity.Uncommon, "Uncommon"), new(Rarity.Rare, "Rare"), new(Rarity.SuperRare, "Super Rare"), new(Rarity.Legendary, "Legendary"), new(Rarity.Mythical, "Mythical")];
    public ICommand CaptureShopCommand { get; }
    public ICommand DecreaseRankCommand { get; }
    public ICommand IncreaseRankCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Warning { get => _warning; private set { Set(ref _warning, value); OnPropertyChanged(nameof(WarningVisibility)); } }
    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(Warning) ? Visibility.Collapsed : Visibility.Visible;
    public string RunSummary { get => _runSummary; private set => Set(ref _runSummary, value); }
    public string Notes { get => _notes; private set => Set(ref _notes, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshCreatureRows(); } }
    public string ItemSearchText { get => _itemSearchText; set { if (Set(ref _itemSearchText, value)) RefreshItemRows(); } }
    public string TrinketSearchText { get => _trinketSearchText; set { if (Set(ref _trinketSearchText, value)) RefreshTrinketRows(); } }
    public RegionFilterOption SelectedRegionFilter { get => _selectedRegionFilter; set { if (Set(ref _selectedRegionFilter, value)) ApplyRegionFilter(); } }
    public RarityFilterOption SelectedRarityFilter { get => _selectedRarityFilter; set { if (Set(ref _selectedRarityFilter, value)) RefreshCreatureRows(); } }
    public RarityFilterOption SelectedItemRarityFilter { get => _selectedItemRarityFilter; set { if (Set(ref _selectedItemRarityFilter, value)) RefreshItemRows(); } }
    public RarityFilterOption SelectedTrinketRarityFilter { get => _selectedTrinketRarityFilter; set { if (Set(ref _selectedTrinketRarityFilter, value)) RefreshTrinketRows(); } }
    public string CreatureCount => $"{CreatureRows.Count} creatures";
    public string ItemCount => $"{ItemRows.Count} items";
    public string TrinketCount => $"{TrinketRows.Count} trinkets";
    public string RankLabel => _snapshot is null ? "Rank —" : $"Rank {_snapshot.ShopRank}";

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
            RefreshItemRows();
            RefreshTrinketRows();
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
            if (_catalog == CatalogSnapshot.Empty)
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
            await SaveHistoryAsync(result with { Snapshot = snapshot, Fields = review.AcceptedFields }, true);
            _capturedSnapshot = snapshot;
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
        _snapshot = snapshot;
        _oddsByMonster = odds.MonsterOdds.ToDictionary(monster => monster.MonsterId, StringComparer.OrdinalIgnoreCase);
        _oddsByItem = odds.ItemOdds.ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        RefreshCreatureRows();
        RefreshItemRows();
        RunSummary = $"Rank {snapshot.ShopRank} · ${snapshot.Gold} · item uses {snapshot.ItemUsesRemaining}/{snapshot.DailyItemLimit} · {snapshot.RegionFilter} · {odds.BatomonSlotsNextShop} Batomon slot(s) next shop";
        Notes = string.Join(" ", odds.Notes);
        OnPropertyChanged(nameof(RankLabel));
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

    private void RefreshItemRows()
    {
        if (_catalog == CatalogSnapshot.Empty) return;
        var search = ItemSearchText.Trim();
        var checkedIds = ItemRows.Where(item => item.IsChecked).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ItemRows.Clear();
        foreach (var item in _catalog.Items
            .Where(item => (SelectedItemRarityFilter.Value is null || item.Rarity == SelectedItemRarityFilter.Value)
                && (string.IsNullOrWhiteSpace(search) || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Rarity).ThenBy(item => item.Name))
        {
            _oddsByItem.TryGetValue(item.Id, out var odds);
            ItemRows.Add(new(item.Id, item.Name, DisplayRarity(item.Rarity), item.Price is { } price ? $"${price}" : "—", item.RuleText ?? "—",
                odds?.PerItemRoll.ToString("P2") ?? "—", odds?.NextShopChance.ToString("P2") ?? "—",
                item.SpritePath is null ? null : Path.Combine(AppContext.BaseDirectory, item.SpritePath.Replace('/', Path.DirectorySeparatorChar)), checkedIds.Contains(item.Id)));
        }
        OnPropertyChanged(nameof(ItemCount));
    }

    private void RefreshTrinketRows()
    {
        if (_catalog == CatalogSnapshot.Empty) return;
        var search = TrinketSearchText.Trim();
        TrinketRows.Clear();
        foreach (var trinket in _catalog.Trinkets
            .Where(trinket => (SelectedTrinketRarityFilter.Value is null || trinket.Rarity == SelectedTrinketRarityFilter.Value)
                && (string.IsNullOrWhiteSpace(search) || trinket.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(trinket => trinket.Rarity).ThenBy(trinket => trinket.Name))
            TrinketRows.Add(new(trinket.Name, DisplayRarity(trinket.Rarity), trinket.RuleText ?? "—", trinket.IsUnique ? "Unique" : "", trinket.SpritePath is null ? null : Path.Combine(AppContext.BaseDirectory, trinket.SpritePath.Replace('/', Path.DirectorySeparatorChar))));
        OnPropertyChanged(nameof(TrinketCount));
    }

    private void ApplyRegionFilter()
    {
        if (_snapshot is null) { RefreshCreatureRows(); return; }
        var snapshot = _snapshot with { RegionFilter = SelectedRegionFilter.Value };
        Render(_oddsCalculator.Calculate(_catalog, snapshot), snapshot);
    }

    private void ChangeRank(int change)
    {
        if (_snapshot is null) { Status = "Capture a shop first to change the rank."; return; }
        var rank = Math.Clamp(_snapshot.ShopRank + change, ShopRankOdds.MinimumRank, ShopRankOdds.MaximumRank);
        if (rank == _snapshot.ShopRank) return;
        var source = _capturedSnapshot ?? _snapshot;
        var snapshot = _snapshot with { ShopRank = rank, RarityOdds = ShopRankOdds.ApplyCapturedModifier(source.ShopRank, source.RarityOdds, rank), CapturedAt = DateTimeOffset.UtcNow };
        Render(_oddsCalculator.Calculate(_catalog, snapshot), snapshot);
        Status = $"Calculated rank {rank}, preserving the captured shop-odds modifier.";
    }

    private static string DisplayRarity(Rarity rarity) => rarity == Rarity.SuperRare ? "Super Rare" : rarity.ToString();

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
public sealed class ItemBrowserRow(string id, string name, string rarity, string price, string effect, string perRoll, string nextShop, string? spritePath, bool isChecked) : INotifyPropertyChanged
{
    private bool _isChecked = isChecked;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Rarity { get; } = rarity;
    public string Price { get; } = price;
    public string Effect { get; } = effect;
    public string PerRoll { get; } = perRoll;
    public string NextShop { get; } = nextShop;
    public string? SpritePath { get; } = spritePath;
    public bool IsChecked { get => _isChecked; set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
}
public sealed record TrinketBrowserRow(string Name, string Rarity, string Effect, string Attribute, string? SpritePath);
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
