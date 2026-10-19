using BatomonHelper.Core;

namespace BatomonHelper.Simulation;

public sealed record LiveBattleAnalysisResult(BoardAnalysisResult Player, BoardAnalysisResult Opponent);

/// <summary>Output projections only. The bridge supplies the game's authoritative fight result.</summary>
public static class LiveBattleAnalysis
{
    public static LiveBattleAnalysisResult Analyze(GameBattleState battle, RulesManifest rules,
        int samples = 1, ulong seed = 69, CancellationToken cancellation = default)
    {
        if (samples is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(samples));
        if (battle.Player.Day != battle.Opponent.Day) throw new InvalidDataException("Battle teams have different days.");
        var player = Import(battle.Player, rules);
        var opponent = Import(battle.Opponent, player.Rules);
        var analyzer = new BoardAnalyzer(opponent.Rules);
        var playerOutput = analyzer.Analyze(player.Board, opponent.Board.MaxHp, samples, seed, cancellation);
        var opponentOutput = analyzer.Analyze(opponent.Board, player.Board.MaxHp, samples, seed, cancellation);
        return new(playerOutput, opponentOutput);
    }

    private static LiveBoardProjection Import(GameBattleSide side, RulesManifest rules) => LiveBoardImport.Create(
        new BoardSnapshot { Day = side.Day, TeamHp = side.TeamHp, TrainerId = side.TrainerId, Team = side.Team,
            ShopRank = side.Rank, Wins = side.Wins }, side.Combat with { Bench = null }, rules);
}
