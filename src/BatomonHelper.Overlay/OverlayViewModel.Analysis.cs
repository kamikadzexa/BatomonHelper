using BatomonHelper.Core;
using BatomonHelper.Simulation;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace BatomonHelper.Overlay;

public sealed partial class OverlayViewModel
{
    private RulesManifest? _damageRules;
    private CancellationTokenSource? _damageCancellation;
    private string? _damageIdentity;
    private string? _positionIdentity;
    private string _damageSummary = "";
    private string _positionHint = "Final placement advice appears when a stronger board or bench layout is found.";
    private PointCollection _damageCurve = [], _damageHpLine = [];
    public ObservableCollection<DamageRateRow> DamageRows { get; } = [];
    public ObservableCollection<DamageRateRow> OpponentDamageRows { get; } = [];
    private string _battleSummary = "";
    public ObservableCollection<BoardCardRow> OpponentRows { get; } = [];
    public string BattleSummary { get => _battleSummary; private set => Set(ref _battleSummary, value); }

    private async Task RefreshBattleAsync(GameBattleState battle)
    {
        BattleSummary = BattleOutcomePresentation.Summary(battle.Outcome);
        var identity = "battle:" + JsonSerializer.Serialize(battle with { Outcome = null });
        if (_damageIdentity == identity) return;
        _damageIdentity = identity;
        _positionIdentity = null;
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
        try
        {
            _damageRules ??= JsonSerializer.Deserialize<RulesManifest>(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json")))
                ?? throw new InvalidDataException("Simulation rules are unavailable.");
            if (cancellation.IsCancellationRequested) return;
            var result = await Task.Run(() => LiveBattleAnalysis.TryAnalyze(battle, _damageRules,
                cancellation: cancellation.Token));
            if (cancellation.IsCancellationRequested || result is null) return;
            foreach (var row in DamagePresentation.Rows(result.Player)) DamageRows.Add(row);
            foreach (var row in DamagePresentation.Rows(result.Opponent)) OpponentDamageRows.Add(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) Warning = "Battle output unavailable: " + error.Message; }
        finally { if (ReferenceEquals(_damageCancellation, cancellation)) _damageCancellation = null; }
    }
    public string DamageSummary { get => _damageSummary; private set => Set(ref _damageSummary, value); }
    public string PositionHint { get => _positionHint; private set => Set(ref _positionHint, value); }
    public Visibility RecommendedPositionsVisibility => RecommendedTeamRows.Count == 6 ? Visibility.Visible : Visibility.Collapsed;
    public PointCollection DamageCurve { get => _damageCurve; private set => Set(ref _damageCurve, value); }
    public PointCollection DamageHpLine { get => _damageHpLine; private set => Set(ref _damageHpLine, value); }

    private async Task RefreshDamageAsync(BoardSnapshot board, GameCombatState? data)
    {
        var identity = JsonSerializer.Serialize(new { board.Team, board.Bench, board.Day, board.TrainerId, board.TeamHp, board.ShopRank, board.Wins, data });
        if (_damageIdentity == identity) return;
        var positionIdentity = identity;
        var refreshPositionAdvice = _positionIdentity != positionIdentity;
        _damageIdentity = identity;
        _damageCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_captureCancellation.Token);
        _damageCancellation = cancellation;
        BattleSummary = "";
        OpponentRows.Clear();
        OpponentDamageRows.Clear();
        DamageRows.Clear(); DamageCurve = []; DamageHpLine = [];
        if (refreshPositionAdvice)
        {
            RecommendedTeamRows.Clear(); OnPropertyChanged(nameof(RecommendedPositionsVisibility));
        }
        if (data is null)
        {
            DamageSummary = "Restart the game to load the updated bridge and enable DPS analysis.";
            if (refreshPositionAdvice) PositionHint = "Position advice needs the live combat stats from the bridge.";
            _damageCancellation = null;
            return;
        }
        DamageSummary = "";
        if (refreshPositionAdvice) PositionHint = data.Bench is null ? "Restart the game after updating the bridge to compare board and bench positions." : "Optimizing the final board and bench placement…";
        try
        {
            _damageRules ??= JsonSerializer.Deserialize<RulesManifest>(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json")))
                ?? throw new InvalidDataException("Simulation rules are unavailable.");
            if (cancellation.IsCancellationRequested) return;
            var rules = _damageRules;
            var output = await Task.Run(() =>
            {
                var projection = LiveBoardImport.Create(board, data, rules);
                var result = new BoardAnalyzer(projection.Rules).TryAnalyze(projection.Board, samples: 32, cancellation: cancellation.Token);
                var advice = result is not null && refreshPositionAdvice && projection.Bench is not null
                    ? new PositionAdvisor(projection.Rules).TryFindBetterPosition(projection.Board, projection.Bench, cancellation: cancellation.Token)
                    : null;
                return (projection, result, advice);
            });
            if (cancellation.IsCancellationRequested || output.result is null) return;
            if (refreshPositionAdvice) _positionIdentity = positionIdentity;
            var result = output.result;
            DamageSummary = DamagePresentation.Summary(result);
            foreach (var row in DamagePresentation.Rows(result)) DamageRows.Add(row);
            var maximum = Math.Max(result.TargetHp, result.Curve.Max(point => point.Damage)) * 1.05;
            DamageCurve = new(result.Curve.Select(point => new Point(point.Seconds / 30 * 600, 150 - point.Damage / maximum * 145)));
            DamageHpLine = new([new Point(0, 150 - result.TargetHp / maximum * 145), new Point(600, 150 - result.TargetHp / maximum * 145)]);
            if (refreshPositionAdvice && output.advice is { } advice)
            {
                PositionHint = $"Final placement reaches target HP {Timing(advice.Recommended)} instead of {Timing(advice.Current)}.";
                FillRecommendedPositions(advice.Slots);
            }
            else if (refreshPositionAdvice && output.projection.Bench is not null) PositionHint = "No final board and bench placement improves the estimated win time or 30-second DPS.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) DamageSummary = "DPS analysis unavailable: " + error.Message; }
        finally { if (ReferenceEquals(_damageCancellation, cancellation)) _damageCancellation = null; }

        static string Timing(BoardAnalysisResult result) => result.MedianKillSeconds is { } seconds
            ? $"at {seconds:0.0}s" : $"not reached by 30s ({result.Checkpoints[^1].Dps:N1} DPS)";
    }

    private void FillRecommendedPositions(Unit?[] slots)
    {
        Fill(RecommendedTeamRows, slots.Take(6), "Board");
        OnPropertyChanged(nameof(RecommendedPositionsVisibility));
        void Fill(ObservableCollection<BoardCardRow> rows, IEnumerable<Unit?> units, string area)
        {
            rows.Clear(); var index = 0;
            foreach (var unit in units)
            {
                index++;
                var monster = unit is null ? null : _catalog.Monsters.FirstOrDefault(value => value.Id == unit.SpeciesId);
                var path = unit?.Shiny == true ? monster?.ShinySpritePath : monster?.SpritePath;
                rows.Add(new($"{area} {index}: {monster?.Name ?? "Empty"}", path is null ? null : Path.Combine(AppContext.BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar)),
                    unit is null ? "" : $"Lv. {unit.Level}", unit?.Shiny == true ? "Shiny" : "", "", ""));
            }
        }
    }
}
