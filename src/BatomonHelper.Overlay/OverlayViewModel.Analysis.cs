using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace BatomonHelper.Overlay;

public sealed partial class OverlayViewModel
{
    private Task<RulesManifest>? _damageRules;
    private CancellationTokenSource? _damageCancellation, _positionCancellation;
    private string? _damageIdentity;
    private string? _positionIdentity;
    private string _damageSummary = "";
    private string _positionHint = "";
    public sealed record PlacementOption(PlacementObjective Key, string Name);
    public IReadOnlyList<PlacementOption> PlacementObjectives { get; } =
        [new(PlacementObjective.FastestKill, "Fastest kill"), new(PlacementObjective.MostHpAndShield, "Most HP + shield")];
    private PointCollection _damageCurve = [], _damageHpLine = [];
    public ObservableCollection<DamageRateRow> DamageRows { get; } = [];
    public ObservableCollection<DamageRateRow> OpponentDamageRows { get; } = [];
    private string _battleSummary = "";
    public ObservableCollection<BoardCardRow> OpponentRows { get; } = [];
    public string BattleSummary { get => _battleSummary; private set => Set(ref _battleSummary, value); }
    public Visibility DpsCountersVisibility => Setup.ShowDpsCounters ? Visibility.Visible : Visibility.Collapsed;

    private async Task RefreshBattleAsync(GameBattleState battle)
    {
        BattleSummary = Setup.ShowBattleOutcome ? BattleOutcomePresentation.Summary(battle.Outcome) : "";
        var identity = "battle:" + JsonSerializer.Serialize(battle with { Outcome = null, Frame = null, Observations = null });
        if (_damageIdentity == identity) return;
        AnalysisWarning = "";
        _damageIdentity = identity;
        ClearPlacement();
        _damageCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_captureCancellation.Token);
        _damageCancellation = cancellation;
        DamageRows.Clear(); OpponentDamageRows.Clear(); DamageCurve = []; DamageHpLine = [];
        RecommendedTeamRows.Clear();
        OnPropertyChanged(nameof(RecommendedPositionsVisibility));
        PositionHint = "";
        DamageSummary = $"Your HP {battle.Player.TeamHp:N0} / Enemy HP {battle.Opponent.TeamHp:N0}";
        OpponentRows.Clear();
        foreach (var slot in battle.Opponent.Team)
        {
            var monster = _catalog.Monsters.FirstOrDefault(value => value.Id == slot.DefinitionId);
            var path = slot.Variant == ShinyState.Shiny ? monster?.ShinySpritePath : monster?.SpritePath;
            OpponentRows.Add(new($"{slot.Index + 1}. {monster?.Name ?? "Empty"}",
                path is null ? null : Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar)),
                slot.Level is null ? "" : $"Lv. {slot.Level}", monster is null ? "" : slot.Variant.ToString(),
                slot.Stats.IsDefault ? "" : string.Join(" / ", slot.Stats.Select(stat => $"{stat.Kind} {stat.Value}")), ""));
        }
        if (!Setup.ShowDpsCounters)
        {
            _damageCancellation = null;
            return;
        }
        try
        {
            var rules = await GetCombatRulesAsync();
            if (cancellation.IsCancellationRequested) return;
            var result = await Task.Run(() => LiveBattleAnalysis.TryAnalyze(battle, rules,
                cancellation: cancellation.Token));
            if (cancellation.IsCancellationRequested || result is null) return;
            foreach (var row in DamagePresentation.Rows(result.Player)) DamageRows.Add(row);
            foreach (var row in DamagePresentation.Rows(result.Opponent)) OpponentDamageRows.Add(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) AnalysisWarning = "Battle output unavailable: " + error.Message; }
        finally { if (ReferenceEquals(_damageCancellation, cancellation)) _damageCancellation = null; }
    }
    public string DamageSummary { get => _damageSummary; private set => Set(ref _damageSummary, value); }
    public string PositionHint { get => _positionHint; private set => Set(ref _positionHint, value); }
    public Visibility PlacementControlsVisibility => Setup.ShowBestDpsBoardSuggestions && _lastLiveBattle is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecommendedPositionsVisibility => Setup.ShowBestDpsBoardSuggestions && RecommendedTeamRows.Count == 6 ? Visibility.Visible : Visibility.Collapsed;
    public PointCollection DamageCurve { get => _damageCurve; private set => Set(ref _damageCurve, value); }
    public PointCollection DamageHpLine { get => _damageHpLine; private set => Set(ref _damageHpLine, value); }

    internal static string CombatIdentity(BoardSnapshot board, GameCombatState data, bool includeBench)
    {
        static string[] Units(IEnumerable<BoardSlot> slots, GameCombatUnit[] combat, bool ordered)
        {
            var details = combat.ToLookup(unit => unit.Slot);
            var identities = slots.OrderBy(slot => slot.Index).Select(slot => JsonSerializer.Serialize(new
            {
                slot.Occupancy, slot.DefinitionId, slot.Variant, slot.Level,
                Combat = details[slot.Index].Select(unit => new
                {
                    unit.PermanentStats, unit.BaseCooldown, unit.AddedTypes
                })
            }));
            return (ordered ? identities : identities.Order(StringComparer.Ordinal)).ToArray();
        }

        return JsonSerializer.Serialize(new
        {
            board.Day, board.TrainerId, board.TeamHp, Rank = ShopRankOdds.Normalize(board.ShopRank ?? 1), board.Wins,
            Team = Units(board.Team, data.Team, ordered: true), data.Trinkets,
            Counters = data.Counters?.OrderBy(counter => counter.Key, StringComparer.Ordinal), data.ExtraTrainers,
            Bench = includeBench && data.Bench is not null ? Units(board.Bench, data.Bench, ordered: false) : null
        });
    }

    private async Task<RulesManifest> GetCombatRulesAsync()
    {
        var load = _damageRules ??= LoadAsync();
        try { return await load; }
        catch { if (ReferenceEquals(load, _damageRules)) _damageRules = null; throw; }

        static async Task<RulesManifest> LoadAsync() => JsonSerializer.Deserialize<RulesManifest>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "combat-rules.json")))
            ?? throw new InvalidDataException("Simulation rules are unavailable.");
    }

    private static (double Hp, bool Estimated) LiveTarget(BoardSnapshot board, RulesManifest rules) =>
        board.Day is int day && rules.Parameters.HpEvidence?.FirstOrDefault(point => point.Day == day && point.Kind != EvidenceKind.Guessed) is { } evidence
            ? (evidence.Value, false) : (board.TeamHp!.Value, true);

    private void RefreshShopAnalysis(BoardSnapshot board, GameCombatState? data)
    {
        BattleSummary = "";
        if (OpponentRows.Count > 0) OpponentRows.Clear();
        if (OpponentDamageRows.Count > 0) OpponentDamageRows.Clear();
        _ = RefreshDamageAsync(board, data);
        _ = RefreshPositionAsync(board, data);
    }

    private async Task RefreshDamageAsync(BoardSnapshot board, GameCombatState? data)
    {
        if (!Setup.ShowDpsCounters) return;
        var identity = data is null ? "shop:missing" : "shop:" + CombatIdentity(board, data, includeBench: false);
        if (_damageIdentity == identity) return;
        _damageIdentity = identity;
        _damageCancellation?.Cancel();
        DamageRows.Clear(); DamageCurve = []; DamageHpLine = [];
        DamageSummary = "";
        AnalysisWarning = "";
        if (data is null)
        {
            DamageSummary = "Restart the game with the updated bridge to enable DPS analysis.";
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_captureCancellation.Token);
        _damageCancellation = cancellation;
        try
        {
            var rules = await GetCombatRulesAsync();
            var target = LiveTarget(board, rules);
            var result = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var projection = LiveBoardImport.Create(board, data with { Bench = null }, rules);
                return new BoardAnalyzer(rules).TryAnalyze(projection.Board, target.Hp, cancellation: cancellation.Token);
            });
            if (cancellation.IsCancellationRequested || result is null) return;
            DamageSummary = DamagePresentation.Summary(result) + (target.Estimated ? " · Estimated target from live team HP" : "");
            foreach (var row in DamagePresentation.Rows(result)) DamageRows.Add(row);
            var maximum = Math.Max(result.TargetHp, result.Curve.Max(point => point.Damage)) * 1.05;
            DamageCurve = new(result.Curve.Select(point => new Point(point.Seconds / 30 * 600, 150 - point.Damage / maximum * 145)));
            DamageHpLine = new([new Point(0, 150 - result.TargetHp / maximum * 145), new Point(600, 150 - result.TargetHp / maximum * 145)]);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) DamageSummary = "DPS analysis unavailable · " + error.Message; }
        finally { if (ReferenceEquals(_damageCancellation, cancellation)) _damageCancellation = null; }
    }

    private void ClearPlacement()
    {
        _positionIdentity = null;
        OnPropertyChanged(nameof(PlacementControlsVisibility));
        _positionCancellation?.Cancel();
        RecommendedTeamRows.Clear();
        OnPropertyChanged(nameof(RecommendedPositionsVisibility));
        PositionHint = "";
    }

    private async Task RefreshPositionAsync(BoardSnapshot board, GameCombatState? data)
    {
        if (!Setup.ShowBestDpsBoardSuggestions) return;
        var objective = Setup.PlacementObjective;
        var identity = data?.Bench is null ? "missing" : objective + CombatIdentity(board, data, includeBench: true);
        if (_positionIdentity == identity) return;
        ClearPlacement();
        _positionIdentity = identity;
        if (data?.Bench is null)
        {
            PositionHint = "Placement advice needs live combat and bench stats from the bridge.";
            return;
        }

        PositionHint = "Comparing board and bench placements…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_captureCancellation.Token);
        _positionCancellation = cancellation;
        try
        {
            var rules = await GetCombatRulesAsync();
            var target = LiveTarget(board, rules);
            var output = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var projection = LiveBoardImport.Create(board, data, rules);
                var advice = new PositionAdvisor(rules).TryFindBetterPosition(projection.Board, projection.Bench!,
                    target.Hp, cancellation.Token, objective);
                return (projection, advice);
            });
            if (cancellation.IsCancellationRequested) return;
            if (output.advice is not { } advice)
            {
                PositionHint = objective == PlacementObjective.FastestKill
                    ? "No placement improves estimated kill time or 30-second DPS."
                    : "No placement improves survival, 30-second DPS, or board occupancy.";
                return;
            }
            PositionHint = advice.RecommendedSurvival is { } survival
                ? $"Under standard pressure, {Survival(survival)}. 30-second DPS {advice.Recommended.Checkpoints[^1].Dps:N1}."
                : $"Final placement reaches target HP {Timing(advice.Recommended)} instead of {Timing(advice.Current)}.";
            if (target.Estimated) PositionHint += " Target HP is estimated from live team HP.";
            if (advice.ExcludesRandomTrinketEffects) PositionHint += " Placement scoring excludes random trinket targets.";
            FillRecommendedPositions(advice, output.projection.Board.Team);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) PositionHint = "Placement unavailable · " + error.Message; }
        finally { if (ReferenceEquals(_positionCancellation, cancellation)) _positionCancellation = null; }

        static string Timing(BoardAnalysisResult result) => result.MedianKillSeconds is { } seconds
            ? $"at {seconds:0.0}s" : $"beyond 30s ({result.Checkpoints[^1].Dps:N1} DPS)";
        static string Survival(PlacementSurvival result) => result.DefeatedAt is { } seconds
            ? $"survives {seconds:0.0}s" : $"{result.Hp:N0} HP + {result.Shield:N0} shield at 30s";
    }

    private void FillRecommendedPositions(PositionAdvice advice, Unit?[] currentTeam)
    {
        Fill(RecommendedTeamRows, advice.Slots.Take(6), "Board");
        OnPropertyChanged(nameof(RecommendedPositionsVisibility));
        string Cooldown(int slot) => advice.Recommended.Checkpoints[0].Units
            .FirstOrDefault(unit => unit.Slot == slot)?.Cooldown is { } cooldown ? $"{cooldown:0.##}s cooldown" : "";

        void Fill(ObservableCollection<BoardCardRow> rows, IEnumerable<Unit?> units, string area)
        {
            rows.Clear(); var index = 0;
            foreach (var unit in units)
            {
                index++;
                var monster = unit is null ? null : _catalog.Monsters.FirstOrDefault(value => value.Id == unit.SpeciesId);
                var path = unit?.Shiny == true ? monster?.ShinySpritePath : monster?.SpritePath;
                rows.Add(new($"{area} {index}: {monster?.Name ?? "Empty"}", path is null ? null : Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar)),
                    unit is null ? "" : $"Lv. {unit.Level}", unit?.Shiny == true ? "Shiny" : "", Cooldown(index - 1), "")
                {
                    IsChanged = unit?.Instance != currentTeam[index - 1]?.Instance
                });
            }
        }
    }
}
