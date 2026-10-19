namespace BatomonHelper.Simulation;

/// <summary>Tests every single legal swap between the six battle slots and four bench slots.
/// A one-swap recommendation is actionable in the live game and avoids claiming that an
/// impractical multi-step rearrangement can be performed without intermediate effects.</summary>
public sealed record PositionAdvice(Unit?[] Slots, BoardAnalysisResult Current, BoardAnalysisResult Recommended);

public sealed class PositionAdvisor(RulesManifest rules)
{
    public PositionAdvice? FindBetterPosition(FrozenBoard current, Unit?[] bench, double? targetHp = null,
        CancellationToken cancellation = default)
    {
        if (bench.Length != 4) throw new ArgumentOutOfRangeException(nameof(bench));
        var slots = current.Team.Select(unit => unit?.Clone()).Concat(bench.Select(unit => unit?.Clone())).ToArray();
        if (slots.Count(unit => unit is not null) < 2) return null;

        var analyzer = new BoardAnalyzer(rules);
        var baseline = analyzer.Analyze(current, targetHp, samples: 1, cancellation: cancellation);
        BoardAnalysisResult? best = null;
        Unit?[]? bestSlots = null;
        for (var first = 0; first < slots.Length; first++)
        for (var second = first + 1; second < slots.Length; second++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (first >= 6 && second >= 6 || slots[first] is null && slots[second] is null) continue;
            var candidateSlots = slots.Select(unit => unit?.Clone()).ToArray();
            (candidateSlots[first], candidateSlots[second]) = (candidateSlots[second], candidateSlots[first]);
            var candidateBoard = current with { Team = candidateSlots.Take(6).Select(unit => unit?.Clone()).ToArray() };
            var candidate = analyzer.Analyze(candidateBoard, targetHp, samples: 1, cancellation: cancellation);
            if (best is null || IsBetter(candidate, best)) { best = candidate; bestSlots = candidateSlots; }
        }
        return best is not null && IsBetter(best, baseline) ? new(bestSlots!, baseline, best) : null;
    }

    private static bool IsBetter(BoardAnalysisResult candidate, BoardAnalysisResult current)
    {
        const double epsilon = .01;
        if (candidate.MedianKillSeconds is { } candidateKill)
        {
            if (current.MedianKillSeconds is not { } currentKill) return true;
            if (candidateKill < currentKill - epsilon) return true;
            if (candidateKill > currentKill + epsilon) return false;
        }
        else if (current.MedianKillSeconds is not null) return false;
        return candidate.Checkpoints[^1].Dps > current.Checkpoints[^1].Dps + epsilon;
    }
}
