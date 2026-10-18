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
    private string _damageSummary = "Waiting for a live shop board.";
    private string _damageLimits = "Assumes the opponent has no healing or shields.";
    private PointCollection _damageCurve = [], _damageHpLine = [];
    public ObservableCollection<DamageRateRow> DamageRows { get; } = [];
    public string DamageSummary { get => _damageSummary; private set => Set(ref _damageSummary, value); }
    public string DamageLimits { get => _damageLimits; private set => Set(ref _damageLimits, value); }
    public PointCollection DamageCurve { get => _damageCurve; private set => Set(ref _damageCurve, value); }
    public PointCollection DamageHpLine { get => _damageHpLine; private set => Set(ref _damageHpLine, value); }

    private async Task RefreshDamageAsync(BoardSnapshot board, GameCombatState? data)
    {
        var identity = JsonSerializer.Serialize(new { board.Team, board.Day, board.TrainerId, board.TeamHp, board.ShopRank, board.Wins, data });
        if (_damageIdentity == identity) return;
        _damageIdentity = identity;
        _damageCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_captureCancellation.Token);
        _damageCancellation = cancellation;
        DamageRows.Clear(); DamageCurve = []; DamageHpLine = [];
        if (data is null)
        {
            DamageSummary = "Restart the game to load the updated bridge and enable DPS analysis.";
            _damageCancellation = null;
            return;
        }
        DamageSummary = "Working out how your team builds up…";
        try
        {
            _damageRules ??= JsonSerializer.Deserialize<RulesManifest>(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "assets", "simulation", "rules.json"), cancellation.Token))
                ?? throw new InvalidDataException("Simulation rules are unavailable.");
            var rules = _damageRules;
            var result = await Task.Run(() =>
            {
                var projection = LiveBoardImport.Create(board, data, rules);
                return new BoardAnalyzer(projection.Rules).Analyze(projection.Board, samples: 32, cancellation: cancellation.Token);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            DamageSummary = DamagePresentation.Summary(result);
            foreach (var row in DamagePresentation.Rows(result)) DamageRows.Add(row);
            var maximum = Math.Max(result.TargetHp, result.Curve.Max(point => point.Damage)) * 1.05;
            DamageCurve = new(result.Curve.Select(point => new Point(point.Seconds / 30 * 600, 150 - point.Damage / maximum * 145)));
            DamageHpLine = new([new Point(0, 150 - result.TargetHp / maximum * 145), new Point(600, 150 - result.TargetHp / maximum * 145)]);
            DamageLimits = "Stacking rates use current stats, cooldowns and multicast. DPS shows damage during a nearby second; at 100%, it uses the last second before sudden death.\n" +
                "Healing is how much your team can produce, even at full HP. Burn, poison and electricity rates show new stacks added; damage from those stacks is already included in DPS.\n" +
                "The opponent does not attack, heal, gain shields or remove your effects. Opponent HP is estimated for the current day. Actual fights can differ.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) DamageSummary = "DPS analysis unavailable: " + error.Message; }
        finally { if (ReferenceEquals(_damageCancellation, cancellation)) _damageCancellation = null; }
    }
}
