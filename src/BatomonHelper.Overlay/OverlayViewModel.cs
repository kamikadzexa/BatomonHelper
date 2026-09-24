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
    private IReadOnlyDictionary<string, TrinketOdds> _oddsByTrinket = new Dictionary<string, TrinketOdds>();
    private readonly HashSet<string> _levelThreeCreatureIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ownedUniqueTrinketIds = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private string _itemSearchText = string.Empty;
    private string _trinketSearchText = string.Empty;
    private string _status = "Loading bundled creature catalog…";
    private string _warning = string.Empty;
    private string _runSummary = "No shop has been captured.";
    private string _notes = string.Empty;
    private bool _itemAvailable = true;
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
        ResetCommand = new RelayCommand(ResetManualState);
        _ = InitializeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CreatureBrowserRow> CreatureRows { get; } = [];
    public ObservableCollection<ItemBrowserRow> ItemRows { get; } = [];
    public ObservableCollection<TrinketBrowserRow> TrinketRows { get; } = [];
    public ObservableCollection<ShopRarityOddsRow> ShopOddsRows { get; } = [];
    public IReadOnlyList<RegionFilterOption> RegionFilters { get; } = [new(MonsterRegionFilter.All, "All regions"), new(MonsterRegionFilter.Jinto, "Jinto region"), new(MonsterRegionFilter.Pantra, "Pantra region")];
    public IReadOnlyList<RarityFilterOption> RarityFilters { get; } = [new(null, "All rarities"), new(Rarity.Common, "Common"), new(Rarity.Uncommon, "Uncommon"), new(Rarity.Rare, "Rare"), new(Rarity.SuperRare, "Super Rare"), new(Rarity.Legendary, "Legendary"), new(Rarity.Mythical, "Mythical")];
    public ICommand CaptureShopCommand { get; }
    public ICommand DecreaseRankCommand { get; }
    public ICommand IncreaseRankCommand { get; }
    public ICommand ResetCommand { get; }
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
    public string ShopOddsLabel => _snapshot is null ? "Shop odds" : $"Rank {_snapshot.ShopRank} shop odds";
    public bool ItemAvailable { get => _itemAvailable; set { if (Set(ref _itemAvailable, value) && _snapshot is not null) SetItemAvailable(value); } }

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
            ResetManualState();
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
            await SaveHistoryAsync(result, true, snapshot, review.AcceptedFields);
            _capturedSnapshot = snapshot;
            _itemAvailable = snapshot.ItemAvailable;
            OnPropertyChanged(nameof(ItemAvailable));
            Render(CalculateOdds(snapshot), snapshot);
            Warning = string.Empty;
            Status = $"Shop captured at {snapshot.CapturedAt:HH:mm:ss}. Press F9 after your next shop change.";
        }
        catch (Exception exception)
        {
            Warning = exception.Message;
            Status = "Shop capture failed; the previous state is unchanged.";
        }
        finally { if (window is not null) window.Opacity = restoreOpacity; }
    }

    private async Task SaveHistoryAsync(ShopCaptureResult result, bool accepted, ShopOddsSnapshot? acceptedSnapshot = null, IEnumerable<RecognizedField>? acceptedFields = null)
    {
        try { await _history.SaveAsync(result, accepted, acceptedSnapshot, acceptedFields); }
        catch (Exception exception) { Warning = $"Capture history could not be saved: {exception.Message}"; }
    }

    private void Render(OddsResult odds, ShopOddsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _oddsByMonster = odds.MonsterOdds.ToDictionary(monster => monster.MonsterId, StringComparer.OrdinalIgnoreCase);
        _oddsByItem = odds.ItemOdds.ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        _oddsByTrinket = odds.TrinketOdds.ToDictionary(trinket => trinket.TrinketId, StringComparer.OrdinalIgnoreCase);
        ShopOddsRows.Clear();
        foreach (var rarityOdds in odds.RarityOdds)
        {
            ShopOddsRows.Add(new(
                DisplayRarity(rarityOdds.Rarity),
                rarityOdds.Probability,
                rarityOdds.EligibleCount,
                DisplayRarity(rarityOdds.RolledAs)));
        }
        RefreshCreatureRows();
        RefreshItemRows();
        RefreshTrinketRows();
        RunSummary = $"Rank {snapshot.ShopRank} · ${snapshot.Gold} · item {(snapshot.ItemAvailable ? "available" : "unavailable")} · {snapshot.RegionFilter} · {odds.BatomonSlotsNextShop} Batomon slot(s) next shop";
        Notes = string.Join(" ", odds.Notes);
        OnPropertyChanged(nameof(RankLabel));
        OnPropertyChanged(nameof(ShopOddsLabel));
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
            CreatureRows.Add(new(monster.Id, monster.Name, DisplayRarity(monster.Rarity), (int)monster.Rarity, string.Join(", ", monster.Regions.OrderBy(region => region)), odds?.PerBatomonRoll, odds?.NextShopChance, sprite, _levelThreeCreatureIds.Contains(monster.Id), SetCreatureLevelThree));
        }
        OnPropertyChanged(nameof(CreatureCount));
    }

    private void RefreshItemRows()
    {
        if (_catalog == CatalogSnapshot.Empty) return;
        var search = ItemSearchText.Trim();
        ItemRows.Clear();
        foreach (var item in _catalog.Items
            .Where(item => (SelectedItemRarityFilter.Value is null || item.Rarity == SelectedItemRarityFilter.Value)
                && (string.IsNullOrWhiteSpace(search) || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Rarity).ThenBy(item => item.Name))
        {
            _oddsByItem.TryGetValue(item.Id, out var odds);
            ItemRows.Add(new(item.Id, item.Name, DisplayRarity(item.Rarity), (int)item.Rarity, item.Price, item.RuleText ?? "—",
                odds?.PerItemRoll, odds?.NextShopChance,
                item.SpritePath is null ? null : Path.Combine(AppContext.BaseDirectory, item.SpritePath.Replace('/', Path.DirectorySeparatorChar))));
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
        {
            _oddsByTrinket.TryGetValue(trinket.Id, out var odds);
            TrinketRows.Add(new(trinket.Id, trinket.Name, DisplayRarity(trinket.Rarity), (int)trinket.Rarity, trinket.RuleText ?? "—", trinket.IsUnique ? "Unique" : "", trinket.IsUnique, _ownedUniqueTrinketIds.Contains(trinket.Id), odds?.PerTrinketRoll, odds?.EligibleCount ?? 0, trinket.SpritePath is null ? null : Path.Combine(AppContext.BaseDirectory, trinket.SpritePath.Replace('/', Path.DirectorySeparatorChar)), SetOwnedUniqueTrinket));
        }
        OnPropertyChanged(nameof(TrinketCount));
    }

    private void ApplyRegionFilter()
    {
        if (_snapshot is null) { RefreshCreatureRows(); return; }
        var snapshot = _snapshot with { RegionFilter = SelectedRegionFilter.Value };
        Render(CalculateOdds(snapshot), snapshot);
    }

    private void ChangeRank(int change)
    {
        if (_snapshot is null) return;
        var rank = Math.Clamp(_snapshot.ShopRank + change, ShopRankOdds.MinimumRank, ShopRankOdds.MaximumRank);
        if (rank == _snapshot.ShopRank) return;
        var source = _capturedSnapshot;
        var odds = source is null ? ShopRankOdds.ForRank(rank) : ShopRankOdds.ApplyCapturedModifier(source.ShopRank, source.RarityOdds, rank);
        var snapshot = _snapshot with { ShopRank = rank, RarityOdds = odds, CapturedAt = DateTimeOffset.UtcNow };
        Render(CalculateOdds(snapshot), snapshot);
        Status = source is null ? $"Manual rank set to {rank}." : $"Calculated rank {rank}, preserving the captured shop-odds modifier.";
    }

    private void SetItemAvailable(bool itemAvailable)
    {
        var snapshot = _snapshot! with { ItemAvailable = itemAvailable, CapturedAt = DateTimeOffset.UtcNow };
        Render(CalculateOdds(snapshot), snapshot);
        Status = itemAvailable ? "Item slot enabled: four Batomon slots." : "Item slot disabled: five Batomon slots.";
    }

    private void ResetManualState()
    {
        if (_catalog == CatalogSnapshot.Empty) return;
        _capturedSnapshot = null;
        _levelThreeCreatureIds.Clear();
        _ownedUniqueTrinketIds.Clear();
        SearchText = ItemSearchText = TrinketSearchText = string.Empty;
        SelectedRegionFilter = RegionFilters[0];
        SelectedRarityFilter = SelectedItemRarityFilter = SelectedTrinketRarityFilter = RarityFilters[0];
        _itemAvailable = true;
        OnPropertyChanged(nameof(ItemAvailable));
        var snapshot = new ShopOddsSnapshot(null, ShopRankOdds.MinimumRank, 0, ShopRankOdds.ForRank(ShopRankOdds.MinimumRank), true, null, DateTimeOffset.UtcNow)
        { RegionFilter = MonsterRegionFilter.All };
        Render(CalculateOdds(snapshot), snapshot);
        Status = "Manual browse reset to Rank 1 with an item available.";
    }

    private static string DisplayRarity(Rarity rarity) => rarity == Rarity.SuperRare ? "Super Rare" : rarity.ToString();

    private OddsResult CalculateOdds(ShopOddsSnapshot snapshot) => _oddsCalculator.Calculate(_catalog, snapshot, new(_levelThreeCreatureIds, _ownedUniqueTrinketIds));

    private void SetCreatureLevelThree(string id, bool isLevelThree)
    {
        if (isLevelThree) _levelThreeCreatureIds.Add(id); else _levelThreeCreatureIds.Remove(id);
        RecalculatePools();
    }

    private void SetOwnedUniqueTrinket(string id, bool isOwned)
    {
        if (isOwned) _ownedUniqueTrinketIds.Add(id); else _ownedUniqueTrinketIds.Remove(id);
        RecalculatePools();
    }

    private void RecalculatePools()
    {
        if (_snapshot is null) { RefreshCreatureRows(); RefreshTrinketRows(); return; }
        Render(CalculateOdds(_snapshot), _snapshot);
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

public sealed class CreatureBrowserRow(string id, string name, string rarity, int rarityOrder, string regions, decimal? perRoll, decimal? nextShop, string? spritePath, bool isLevelThree, Action<string, bool> levelThreeChanged) : INotifyPropertyChanged
{
    private bool _isLevelThree = isLevelThree;
    private readonly Action<string, bool> _levelThreeChanged = levelThreeChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Rarity { get; } = rarity;
    public int RarityOrder { get; } = rarityOrder;
    public string Regions { get; } = regions;
    public decimal PerRollValue { get; } = perRoll ?? -1m;
    public decimal NextShopValue { get; } = nextShop ?? -1m;
    public string PerRoll => perRoll?.ToString("P2") ?? "—";
    public string NextShop => nextShop?.ToString("P2") ?? "—";
    public string? SpritePath { get; } = spritePath;
    public bool IsLevelThree { get => _isLevelThree; set { if (_isLevelThree == value) return; _isLevelThree = value; PropertyChanged?.Invoke(this, new(nameof(IsLevelThree))); _levelThreeChanged(Id, value); } }
}

public sealed class ItemBrowserRow(string id, string name, string rarity, int rarityOrder, int? price, string effect, decimal? perRoll, decimal? nextShop, string? spritePath)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Rarity { get; } = rarity;
    public int RarityOrder { get; } = rarityOrder;
    public int PriceValue { get; } = price ?? -1;
    public string Price => price is { } value ? $"${value}" : "—";
    public string Effect { get; } = effect;
    public decimal PerRollValue { get; } = perRoll ?? -1m;
    public decimal NextShopValue { get; } = nextShop ?? -1m;
    public string PerRoll => perRoll?.ToString("P2") ?? "—";
    public string NextShop => nextShop?.ToString("P2") ?? "—";
    public string? SpritePath { get; } = spritePath;
}
public sealed class TrinketBrowserRow(string id, string name, string rarity, int rarityOrder, string effect, string attribute, bool isUnique, bool isOwned, decimal? chance, int poolCount, string? spritePath, Action<string, bool> ownedChanged) : INotifyPropertyChanged
{
    private bool _isOwned = isOwned;
    private readonly Action<string, bool> _ownedChanged = ownedChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Rarity { get; } = rarity;
    public int RarityOrder { get; } = rarityOrder;
    public string Effect { get; } = effect;
    public string Attribute { get; } = attribute;
    public bool IsUnique { get; } = isUnique;
    public decimal ChanceValue { get; } = chance ?? -1m;
    public string Chance => chance?.ToString("P2") ?? "—";
    public int PoolCount { get; } = poolCount;
    public string? SpritePath { get; } = spritePath;
    public bool IsOwned { get => _isOwned; set { if (_isOwned == value) return; _isOwned = value; PropertyChanged?.Invoke(this, new(nameof(IsOwned))); _ownedChanged(Id, value); } }
}
public sealed record RegionFilterOption(MonsterRegionFilter Value, string Name);
public sealed record RarityFilterOption(Rarity? Value, string Name);
public sealed record ShopRarityOddsRow(string Rarity, decimal Probability, int EligibleCount, string RollsAs)
{
    public string Percentage => Probability.ToString("P0");
    public string PoolDescription => EligibleCount == 1 ? "1 eligible creature" : $"{EligibleCount} eligible creatures";
    public string RollDescription => RollsAs == Rarity ? "Direct rarity roll" : $"Falls back to {RollsAs}";
}

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
