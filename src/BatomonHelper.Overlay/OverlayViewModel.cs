using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BatomonHelper.Overlay;

public sealed record BoardCardRow(string Name, string? SpritePath, string Level, string Variant, string Stats, string Price);

public sealed partial class OverlayViewModel : INotifyPropertyChanged
{
    private readonly SnapshotRepository _repository;
    private readonly GameStateBridge _bridge = new();
    private readonly CaptureRefreshQueue _captureQueue;
    private readonly CancellationTokenSource _captureCancellation = new();
    private string? _lastBoardSignature;
    private bool _manualScenario;
    private string _boardSummary = "Waiting for the BatomonHelper bridge mod.";
    private string? _trainerSpritePath;
    private readonly IOddsCalculator _oddsCalculator = new OddsCalculator();
    private CatalogSnapshot _catalog = CatalogSnapshot.Empty;
    private ShopOddsSnapshot? _snapshot;
    private ShopOddsSnapshot? _capturedSnapshot;
    private PoolContext? _poolContext;
    private BoardSnapshot? _acceptedBoard;
    public PoolContext? DisplayedPoolContext => _poolContext;
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
    private string _runSummary = "No shop state received yet.";
    private string _notes = string.Empty;
    private bool _itemAvailable = true;
    private bool _autoUpdate = true;
    private bool _captureRun;
    private RunCaptureStore? _runCaptureStore;
    private string _runCaptureStatus = "Run capture is off.";
    private long _lastLiveSequence;
    private GameCombatState? _lastLiveCombat;
    private RegionFilterOption _selectedRegionFilter = new(MonsterRegionFilter.Pantra, "Pantra region");
    private RarityFilterOption _selectedRarityFilter = new(null, "All rarities");
    private RarityFilterOption _selectedItemRarityFilter = new(null, "All rarities");
    private RarityFilterOption _selectedTrinketRarityFilter = new(null, "All rarities");

    public OverlayViewModel()
    {
        _repository = new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "assets", "wiki", "batomon-wiki.db"), readOnly: true);
        _captureQueue = new(_ => CaptureBoardOnceAsync(), _captureCancellation.Token);
        CaptureShopCommand = new AsyncCommand(CaptureShopAsync);
        DecreaseRankCommand = new RelayCommand(() => ChangeRank(-1));
        IncreaseRankCommand = new RelayCommand(() => ChangeRank(1));
        ResetCommand = new RelayCommand(ResetManualState);
        _ = Application.Current.Dispatcher.InvokeAsync(InitializeAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CreatureBrowserRow> CreatureRows { get; } = [];
    public ObservableCollection<ItemBrowserRow> ItemRows { get; } = [];
    public ObservableCollection<TrinketBrowserRow> TrinketRows { get; } = [];
    public ObservableCollection<ShopRarityOddsRow> ShopOddsRows { get; } = [];
    public ObservableCollection<BoardCardRow> TeamRows { get; } = [];
    public ObservableCollection<BoardCardRow> BenchRows { get; } = [];
    public ObservableCollection<BoardCardRow> OfferRows { get; } = [];
    public ObservableCollection<BoardCardRow> RecommendedTeamRows { get; } = [];
    public ObservableCollection<BoardCardRow> RecommendedBenchRows { get; } = [];
    public string BoardSummary { get => _boardSummary; private set => Set(ref _boardSummary, value); }
    public string? TrainerSpritePath { get => _trainerSpritePath; private set => Set(ref _trainerSpritePath, value); }
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
    public RegionFilterOption SelectedRegionFilter
    {
        get => _selectedRegionFilter;
        set
        {
            if (Set(ref _selectedRegionFilter, value)) ApplyRegionFilter();
        }
    }
    public RarityFilterOption SelectedRarityFilter { get => _selectedRarityFilter; set { if (Set(ref _selectedRarityFilter, value)) RefreshCreatureRows(); } }
    public RarityFilterOption SelectedItemRarityFilter { get => _selectedItemRarityFilter; set { if (Set(ref _selectedItemRarityFilter, value)) RefreshItemRows(); } }
    public RarityFilterOption SelectedTrinketRarityFilter { get => _selectedTrinketRarityFilter; set { if (Set(ref _selectedTrinketRarityFilter, value)) RefreshTrinketRows(); } }
    public string CreatureCount => $"{CreatureRows.Count} creatures";
    public string CatalogVersionText => $"Wiki {_catalog.WikiVersion ?? "snapshot"} · build {_catalog.DeclaredSteamBuild} · Balance {_catalog.Balance ?? "unknown"}" +
        (_catalog.ReferencePatchVersion is null ? "" : $" · reference patch {_catalog.ReferencePatchVersion}");
    public string ItemCount => $"{ItemRows.Count} items";
    public string TrinketCount => $"{TrinketRows.Count} trinkets";
    public string RankLabel => _snapshot is null ? "Rank —" : $"Rank {_snapshot.ShopRank}";
    public string ShopOddsLabel => _snapshot is null ? "Shop odds" : $"Rank {_snapshot.ShopRank} shop odds";
    public bool ItemAvailable { get => _itemAvailable; set { if (Set(ref _itemAvailable, value) && _snapshot is not null) SetItemAvailable(value); } }
    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (!Set(ref _autoUpdate, value)) return;
            if (!value && CaptureRun) StopRunCapture();
            if (value) _ = CaptureShopAsync();
            OnPropertyChanged(nameof(CanToggleRunCapture));
        }
    }
    public bool CaptureRun
    {
        get => _captureRun;
        set
        {
            if (value == _captureRun) return;
            if (value)
            {
                if (!CanStartRunCapture())
                {
                    Warning = "Run capture can start only with automatic update on, from the clean Day 1 opening state.";
                    return;
                }
                StartRunCapture();
            }
            else StopRunCapture();
        }
    }
    public bool CanToggleRunCapture => _captureRun || CanStartRunCapture();
    public string RunCaptureStatus { get => _runCaptureStatus; private set => Set(ref _runCaptureStatus, value); }

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
            OnPropertyChanged(nameof(CatalogVersionText));
            RefreshItemRows();
            RefreshTrinketRows();
            ResetManualState();
            Status = $"Loaded {_catalog.Monsters.Length} bundled creatures. Waiting for the game bridge. F9 refreshes manually.";
            _ = PollGameStateAsync();
        }
        catch (Exception exception)
        {
            Warning = $"The bundled wiki catalog could not be opened: {exception.Message}";
            Status = "Bundled catalog unavailable.";
        }
    }

    public async Task CaptureShopAsync()
    {
        _lastBoardSignature = null;
        await _captureQueue.RequestAsync();
    }

    public void ReportHotkeyConflict(string message) => Warning = message;

    public void StopCaptures()
    {
        if (CaptureRun) StopRunCapture();
        _captureCancellation.Cancel();
    }

    private async Task PollGameStateAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            if (AutoUpdate) await _captureQueue.RequestAsync();
            while (await timer.WaitForNextTickAsync(_captureCancellation.Token))
                if (AutoUpdate) await _captureQueue.RequestAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task CaptureBoardOnceAsync()
    {
        try
        {
            if (_catalog == CatalogSnapshot.Empty) return;
            var message = await _bridge.ReadAsync(_catalog, _captureCancellation.Token);
            if (message.Phase == "battle" && message.Battle is { } battle)
            {
                _ = RefreshBattleAsync(battle);
                Warning = "";
                Status = "Live battle · analysis uses both teams at battle start";
                return;
            }
            if (message.Board is not { } board)
            {
                Warning = "";
                Status = "Game connected; outside the shop. Showing the last received board, if any.";
                return;
            }
            // The bridge exports the active run region. Keep All only when an older
            // game build cannot expose it, so its wider pool remains explicit.
            var capturedBoard = board;
            _lastLiveSequence = message.Sequence;
            _lastLiveCombat = message.Combat;
            if (_captureRun && _runCaptureStore is not null)
                await SaveRunChangeAsync(_runCaptureStore, message.Sequence, capturedBoard, message.Combat);
            board = capturedBoard;
            SyncCapturedRegion(board.RegionFilter);
            var signature = System.Text.Json.JsonSerializer.Serialize(board with { CapturedAt = default }, GameStateBridge.JsonOptions);
            if (signature != _lastBoardSignature)
            {
                var snapshot = board.ToShopOddsSnapshot(_catalog);
                _acceptedBoard = board;
                var odds = CalculateOdds(snapshot);
                _capturedSnapshot = snapshot;
                _itemAvailable = snapshot.ItemAvailable;
                OnPropertyChanged(nameof(ItemAvailable));
                Render(odds, snapshot);
                RenderBoard(board);
                _lastBoardSignature = signature;
                _manualScenario = false;
                OnPropertyChanged(nameof(CanToggleRunCapture));
            }
            _ = RefreshDamageAsync(board, message.Combat);
            Warning = "";
            Status = _manualScenario ? "Manual odds preview · game bridge connected · F9 restores live values"
                : $"Live game state · updated {board.CapturedAt.ToLocalTime():HH:mm:ss} · automatic refresh";
        }
        catch (OperationCanceledException) { }
        catch (FileNotFoundException) { BridgeUnavailable("Install the BatomonHelper bridge mod and start the game."); }
        catch (DirectoryNotFoundException) { BridgeUnavailable("Install the BatomonHelper bridge mod and start the game."); }
        catch (Exception exception) { BridgeUnavailable(exception.Message); }
    }

    private void BridgeUnavailable(string reason)
    {
        Warning = reason;
        Status = "Game bridge disconnected or unavailable. Displayed board is the last received state.";
    }
    private void RenderBoard(BoardSnapshot board)
    {
        var trainer = _catalog.Trainers.First(entry => entry.Id == board.TrainerId);
        TrainerSpritePath = trainer.SpritePath is null ? null : Path.Combine(AppContext.BaseDirectory, trainer.SpritePath.Replace('/', Path.DirectorySeparatorChar));
        BoardSummary = $"{trainer.Name} · Day {board.Day} · Lives {board.Lives} · Wins {board.Wins}/{board.WinTarget} · HP {board.TeamHp} · ${board.Gold} · Rank {board.ShopRank}\n" +
            (board.FreeRerolls is null ? $"Reroll ${board.RerollCost}" : $"Free rerolls {board.FreeRerolls}") +
            (board.ItemUsesCounterVisible ? $" · Item uses {board.ItemUsesRemaining}/{board.ItemUsesMaximum}" : " · Item counter absent") +
            (board.ShopLocked == true ? " · Shop locked" : " · Shop unlocked");
        Fill(TeamRows, board.Team); Fill(BenchRows, board.Bench); Fill(OfferRows, board.Shop);
        void Fill(ObservableCollection<BoardCardRow> rows, IEnumerable<BoardSlot> slots)
        {
            rows.Clear();
            foreach (var slot in slots)
            {
                var creature = _catalog.Monsters.FirstOrDefault(entry => entry.Id == slot.DefinitionId);
                var item = _catalog.Items.FirstOrDefault(entry => entry.Id == slot.DefinitionId);
                var name = creature?.Name ?? item?.Name ?? "Empty";
                var path = slot.Variant == ShinyState.Shiny ? creature?.ShinySpritePath : creature?.SpritePath ?? item?.SpritePath;
                var sprite = path is null ? null : Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                rows.Add(new($"{slot.Index + 1}. {name}", sprite, slot.Level is null ? "" : $"Lv. {slot.Level}", creature is null ? "" : slot.Variant.ToString(),
                    slot.Stats.IsDefault ? "" : string.Join(" · ", slot.Stats.Select(stat => $"{stat.Kind} {stat.Value}")),
                    slot.Price is null ? "" : $"${slot.Price}" + (slot.Affordable == false ? " · unavailable" : "")));
            }
        }
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
            var reference = string.Join("\n", monster.ReferenceValues.Select(value => $"{value.Key}: {value.Value}"));
            if (!monster.ReferenceNotes.IsEmpty) reference += "\n\n" + string.Join("\n", monster.ReferenceNotes) + "\n" + _catalog.SourceUrls.LastOrDefault();
            CreatureRows.Add(new(monster.Id, monster.Name, DisplayRarity(monster.Rarity), (int)monster.Rarity, string.Join(", ", monster.Regions.OrderBy(region => region)), odds?.PerBatomonRoll, odds?.NextShopChance, sprite, _poolContext?.LevelThreeMonsterIds.Contains(monster.Id) ?? _levelThreeCreatureIds.Contains(monster.Id), SetCreatureLevelThree, reference));
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

    private void SyncCapturedRegion(MonsterRegionFilter region)
    {
        var option = RegionFilters.First(candidate => candidate.Value == region);
        Set(ref _selectedRegionFilter, option, nameof(SelectedRegionFilter));
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
        _manualScenario = true;
        OnPropertyChanged(nameof(CanToggleRunCapture));
        Status = source is null ? $"Manual rank set to {rank}." : $"Calculated rank {rank}, preserving the captured shop-odds modifier.";
    }

    private void SetItemAvailable(bool itemAvailable)
    {
        var snapshot = _snapshot! with { ItemAvailable = itemAvailable, CapturedAt = DateTimeOffset.UtcNow };
        Render(CalculateOdds(snapshot), snapshot);
        _manualScenario = true;
        OnPropertyChanged(nameof(CanToggleRunCapture));
        Status = itemAvailable ? "Item slot enabled: four Batomon slots." : "Item slot disabled: five Batomon slots.";
    }

    private void ResetManualState()
    {
        if (_catalog == CatalogSnapshot.Empty) return;
        _lastBoardSignature = null;
        _capturedSnapshot = null;
        _levelThreeCreatureIds.Clear();
        _ownedUniqueTrinketIds.Clear();
        SearchText = ItemSearchText = TrinketSearchText = string.Empty;
        SelectedRegionFilter = RegionFilters.Single(option => option.Value == MonsterRegionFilter.Pantra);
        SelectedRarityFilter = SelectedItemRarityFilter = SelectedTrinketRarityFilter = RarityFilters[0];
        _itemAvailable = true;
        OnPropertyChanged(nameof(ItemAvailable));
        var snapshot = new ShopOddsSnapshot(null, ShopRankOdds.MinimumRank, 0, ShopRankOdds.ForRank(ShopRankOdds.MinimumRank), true, null, DateTimeOffset.UtcNow)
        { RegionFilter = MonsterRegionFilter.Pantra };
        Render(CalculateOdds(snapshot), snapshot);
        _manualScenario = true;
        OnPropertyChanged(nameof(CanToggleRunCapture));
        Status = "Manual browse reset to Rank 1 with an item available.";
    }

    private bool CanStartRunCapture() => AutoUpdate && !_manualScenario && _acceptedBoard is { Day: 1, Wins: 0 } board
        && board.ShopLocked == false
        && board.Team.All(slot => slot.Occupancy == SlotOccupancy.Empty)
        && board.Bench.All(slot => slot.Occupancy == SlotOccupancy.Empty);

    private void StartRunCapture()
    {
        var board = _acceptedBoard!;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BatomonHelper", "runs");
        var store = new RunCaptureStore(directory, _catalog, DateTimeOffset.UtcNow);
        _runCaptureStore = store;
        _captureRun = true;
        OnPropertyChanged(nameof(CaptureRun));
        OnPropertyChanged(nameof(CanToggleRunCapture));
        Warning = "";
        RunCaptureStatus = $"Recording training run to {store.Path}";
        _ = SaveRunChangeAsync(store, _lastLiveSequence, board, _lastLiveCombat);
    }

    private void StopRunCapture()
    {
        var store = _runCaptureStore;
        _runCaptureStore = null;
        _captureRun = false;
        OnPropertyChanged(nameof(CaptureRun));
        OnPropertyChanged(nameof(CanToggleRunCapture));
        if (store is null) return;
        RunCaptureStatus = $"Saving captured run to {store.Path}";
        _ = CompleteRunCaptureAsync(store);
    }

    private async Task SaveRunChangeAsync(RunCaptureStore store, long sequence, BoardSnapshot board, GameCombatState? combat)
    {
        try { await store.AppendAsync(sequence, board, combat, _captureCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Warning = $"Run capture could not be saved: {exception.Message}";
            if (ReferenceEquals(store, _runCaptureStore)) StopRunCapture();
        }
    }

    private async Task CompleteRunCaptureAsync(RunCaptureStore store)
    {
        try
        {
            await store.CompleteAsync();
            RunCaptureStatus = $"Saved training run: {store.Path}";
        }
        catch (Exception exception) { Warning = $"Run capture could not be finalized: {exception.Message}"; }
    }

    private static string DisplayRarity(Rarity rarity) => rarity == Rarity.SuperRare ? "Super Rare" : rarity.ToString();

    private OddsResult CalculateOdds(ShopOddsSnapshot snapshot)
    {
        _poolContext = PoolContext.Create(_catalog, snapshot, _acceptedBoard,
            _levelThreeCreatureIds, _ownedUniqueTrinketIds, _oddsCalculator);
        return _poolContext.Odds;
    }

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

public sealed class CreatureBrowserRow(string id, string name, string rarity, int rarityOrder, string regions, decimal? perRoll, decimal? nextShop, string? spritePath, bool isLevelThree, Action<string, bool> levelThreeChanged, string referenceTooltip = "") : INotifyPropertyChanged
{
    private bool _isLevelThree = isLevelThree;
    private readonly Action<string, bool> _levelThreeChanged = levelThreeChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Rarity { get; } = rarity;
    public int RarityOrder { get; } = rarityOrder;
    public string Regions { get; } = regions;
    public string ReferenceTooltip { get; } = referenceTooltip;
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
