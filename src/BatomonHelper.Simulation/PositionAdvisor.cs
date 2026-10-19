namespace BatomonHelper.Simulation;

/// <summary>Finds the strongest reachable final layout by repeatedly taking the best legal
/// board/bench swap. The returned slots are the final placement, not merely the first move.</summary>
public sealed record PositionAdvice(Unit?[] Slots, BoardAnalysisResult Current, BoardAnalysisResult Recommended);

public sealed class PositionAdvisor(RulesManifest rules)
{
    public PositionAdvice? FindBetterPosition(FrozenBoard current, Unit?[] bench, double? targetHp = null,
        CancellationToken cancellation = default)
    {
        var advice = TryFindBetterPosition(current, bench, targetHp, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return advice;
    }

    /// <summary>Superseded searches return no advice, without throwing or publishing a partial search.</summary>
    public PositionAdvice? TryFindBetterPosition(FrozenBoard current, Unit?[] bench, double? targetHp = null,
        CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return null;
        if (bench.Length != 4) throw new ArgumentOutOfRangeException(nameof(bench));
        var slots = current.Team.Select(unit => unit?.Clone()).Concat(bench.Select(unit => unit?.Clone())).ToArray();
        if (slots.Count(unit => unit is not null) < 2) return null;

        var analyzer = new BoardAnalyzer(rules);
        var baseline = analyzer.TryAnalyze(current, targetHp, samples: 1, cancellation: cancellation);
        if (baseline is null) return null;
        var recommended = baseline;
        var improved = false;

        // A change to one position can unlock a better second swap. Continue until the
        // layout is stable so the overlay can show the final actionable placement.
        while (true)
        {
            BoardAnalysisResult? best = null;
            Unit?[]? bestSlots = null;
            for (var first = 0; first < slots.Length; first++)
            for (var second = first + 1; second < slots.Length; second++)
            {
                if (cancellation.IsCancellationRequested) return null;
                if (first >= 6 && second >= 6 || slots[first] is null && slots[second] is null) continue;
                var candidateSlots = slots.Select(unit => unit?.Clone()).ToArray();
                (candidateSlots[first], candidateSlots[second]) = (candidateSlots[second], candidateSlots[first]);
                var candidateBoard = current with { Team = candidateSlots.Take(6).Select(unit => unit?.Clone()).ToArray() };
                var candidate = analyzer.TryAnalyze(candidateBoard, targetHp, samples: 1, cancellation: cancellation);
                if (candidate is null) return null;
                if (IsBetter(candidate, recommended) && (best is null || IsBetter(candidate, best)))
                {
                    best = candidate;
                    bestSlots = candidateSlots;
                }
            }
            if (best is null || bestSlots is null) break;
            slots = bestSlots;
            recommended = best;
            improved = true;
        }
        return !cancellation.IsCancellationRequested && improved ? new(slots, baseline, recommended) : null;
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
