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
        var player = LiveBoardImport.CreateBattleSide(battle.Player, rules);
        var opponent = LiveBoardImport.CreateBattleSide(battle.Opponent, rules, mirrored: true);
        var analyzer = new BoardAnalyzer(opponent.Rules);
        var playerOutput = analyzer.TryAnalyze(player.Board, opponent.Board.MaxHp, samples, seed, cancellation);
        if (playerOutput is null) return null;
        var opponentOutput = analyzer.TryAnalyze(opponent.Board, player.Board.MaxHp, samples, seed, cancellation);
        if (opponentOutput is null || cancellation.IsCancellationRequested) return null;
        return new(playerOutput, opponentOutput);
    }

}
