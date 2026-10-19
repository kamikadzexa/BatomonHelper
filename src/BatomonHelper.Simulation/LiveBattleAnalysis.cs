using BatomonHelper.Core;

namespace BatomonHelper.Simulation;

public sealed record LiveBattleAnalysisResult(BoardAnalysisResult Player, BoardAnalysisResult Opponent);

/// <summary>Output projections only. The bridge supplies the game's authoritative fight result.</summary>
public static class LiveBattleAnalysis
{
    public static LiveBattleAnalysisResult Analyze(GameBattleState battle, RulesManifest rules,
        int samples = 1, ulong seed = 69, CancellationToken cancellation = default)
        => TryAnalyze(battle, rules, samples, seed, cancellation)
            ?? throw new OperationCanceledException(cancellation);

    public static LiveBattleAnalysisResult? TryAnalyze(GameBattleState battle, RulesManifest rules,
        int samples = 1, ulong seed = 69, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return null;
        if (samples is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(samples));
        if (battle.Player.Day != battle.Opponent.Day) throw new InvalidDataException("Battle teams have different days.");
        var player = Import(battle.Player, rules);
        var opponent = Import(battle.Opponent, player.Rules);
        var analyzer = new BoardAnalyzer(opponent.Rules);
        var playerOutput = analyzer.TryAnalyze(player.Board, opponent.Board.MaxHp, samples, seed, cancellation);
        if (playerOutput is null) return null;
        var opponentOutput = analyzer.TryAnalyze(opponent.Board, player.Board.MaxHp, samples, seed, cancellation);
        if (opponentOutput is null || cancellation.IsCancellationRequested) return null;
        return new(playerOutput, opponentOutput);
    }

    private static LiveBoardProjection Import(GameBattleSide side, RulesManifest rules) => LiveBoardImport.Create(
        new BoardSnapshot { Day = side.Day, TeamHp = side.TeamHp, TrainerId = side.TrainerId, Team = side.Team,
            ShopRank = side.Rank, Wins = side.Wins }, side.Combat with { Bench = null }, rules);
}
