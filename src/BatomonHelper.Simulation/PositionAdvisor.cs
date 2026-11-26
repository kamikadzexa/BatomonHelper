using BatomonHelper.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BatomonHelper.Simulation;

public sealed record PlacementSurvival(double Hp, double Shield, double? DefeatedAt)
{
    public double Remaining => Hp + Shield;
}

public sealed record PositionAdvice(Unit?[] Slots, BoardAnalysisResult Current, BoardAnalysisResult Recommended)
{
    public bool ExcludesRandomTrinketEffects { get; init; }
    public PlacementSurvival? CurrentSurvival { get; init; }
    public PlacementSurvival? RecommendedSurvival { get; init; }
}

/// <summary>Searches successive legal board and bench swaps until none improves the selected objective.</summary>
public sealed class PositionAdvisor(RulesManifest rules)
{
    private sealed record Score(FrozenBoard Board, double? Kill, PlacementSurvival? Survival)
    {
        public double? Damage { get; set; }
    }

    public PositionAdvice? FindBetterPosition(FrozenBoard current, Unit?[] bench, double? targetHp = null,
        CancellationToken cancellation = default, PlacementObjective objective = PlacementObjective.FastestKill)
    {
        var advice = TryFindBetterPosition(current, bench, targetHp, cancellation, objective);
        cancellation.ThrowIfCancellationRequested();
        return advice;
    }

    public PositionAdvice? TryFindBetterPosition(FrozenBoard current, Unit?[] bench, double? targetHp = null,
        CancellationToken cancellation = default, PlacementObjective objective = PlacementObjective.FastestKill)
    {
        if (cancellation.IsCancellationRequested) return null;
        if (bench.Length != 4) throw new ArgumentOutOfRangeException(nameof(bench));
        var slots = current.Team.Concat(bench).ToArray();
        if (slots.OfType<Unit>().Count() < 2) return null;

        var randomIds = rules.Trinkets.Where(trinket => trinket.Effects.Any(IsRandomTarget))
            .Select(trinket => trinket.Id).ToHashSet(StringComparer.Ordinal);
        var excludesRandom = current.Bag.Any(entry => randomIds.Contains(entry.Id));
        var placementRules = !excludesRandom ? rules : rules with
        {
            Hash = rules.Hash + ":placement-without-random-trinket-targets",
            Trinkets = rules.Trinkets.Select(trinket => trinket with
            {
                Effects = trinket.Effects.Where(effect => !IsRandomTarget(effect)).ToArray()
            }).ToArray()
        };
        var hp = targetHp ?? (current.Day > rules.Parameters.DailyHp.Length
            ? current.MaxHp : rules.Parameters.DailyHp[Math.Max(0, current.Day - 1)]);
        if (!double.IsFinite(hp) || hp <= 0) throw new ArgumentOutOfRangeException(nameof(targetHp));
        const string targetTrainer = "__passive_target__";
        var target = new FrozenBoard(current.Day, hp, targetTrainer, new Unit?[6], [], []);
        var resolver = new Combat(placementRules with
        {
            Trainers = [.. placementRules.Trainers, new(targetTrainer, "Passive target", 0, false, 0, [], "analysis", "")]
        });
        var identities = slots.OfType<Unit>().Distinct().ToDictionary(unit => unit, UnitIdentity);
        var uniqueIds = identities.Values.Distinct(StringComparer.Ordinal).Select((identity, index) => (identity, index: index + 1))
            .ToDictionary(item => item.identity, item => item.index, StringComparer.Ordinal);
        var scores = new ConcurrentDictionary<string, Lazy<Score>>(StringComparer.Ordinal);
        Score Evaluate(Unit?[] layout)
        {
            var active = layout.Take(6).ToArray();
            var key = string.Join(",", active.Select(unit => unit is null ? 0 : uniqueIds[identities[unit]]));
            return scores.GetOrAdd(key, _ => new Lazy<Score>(() => ScoreLayout(active))).Value;
        }

        Score ScoreLayout(Unit?[] active)
        {
            var board = current with { Team = active };
            var stopAtTarget = objective == PlacementObjective.FastestKill;
            var probe = Simulate(board, stopAtTarget);
            return new(board, probe.KillSeconds, probe.Survival)
            {
                Damage = stopAtTarget && probe.KillSeconds.HasValue ? null : probe.TotalDamage
            };
        }

        CombatProbe Simulate(FrozenBoard board, bool stopAtTarget)
        {
            var probe = new CombatProbe
            {
                Duration = 30, ScoreOnly = true, Cancellation = cancellation, StopQuietlyOnCancellation = true,
                KillTargetHp = hp, StopAtTarget = stopAtTarget,
                PressureHp = objective == PlacementObjective.MostHpAndShield ? hp : null
            };
            resolver.ResolveBattle(board, target, 69, probe: probe);
            return probe;
        }
        double Damage(Score score) => score.Damage ??= Simulate(score.Board, stopAtTarget: false).TotalDamage;


        var baseline = Evaluate(slots);
        var recommended = baseline;
        while (!cancellation.IsCancellationRequested)
        {
            var swapOrder = Enumerable.Range(0, 6).Concat(Enumerable.Range(6, bench.Length)
                .OrderBy(index => slots[index] is { } unit ? identities[unit] : "", StringComparer.Ordinal)).ToArray();
            var neighbors = new List<Unit?[]>();
            for (var first = 0; first < 6; first++)
            foreach (var second in swapOrder.Skip(first + 1))
            {
                if (ReferenceEquals(slots[first], slots[second])) continue;
                var candidateSlots = (Unit?[])slots.Clone();
                (candidateSlots[first], candidateSlots[second]) = (candidateSlots[second], candidateSlots[first]);
                neighbors.Add(candidateSlots);
            }

            var candidateScores = new Score?[neighbors.Count];
            Parallel.For(0, neighbors.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                (index, loop) =>
                {
                    if (cancellation.IsCancellationRequested) loop.Stop();
                    else candidateScores[index] = Evaluate(neighbors[index]);
                });
            if (cancellation.IsCancellationRequested) return null;

            Unit?[]? bestSlots = null;
            var best = recommended;
            for (var index = 0; index < neighbors.Count; index++)
            {
                if (cancellation.IsCancellationRequested) return null;
                var candidate = candidateScores[index]!;
                if (ReferenceEquals(candidate, best) || !IsBetter(candidate, best, Damage)) continue;
                best = candidate;
                bestSlots = neighbors[index];
            }
            if (bestSlots is null) break;
            slots = bestSlots;
            recommended = best;
        }

        if (cancellation.IsCancellationRequested || ReferenceEquals(baseline, recommended)) return null;
        var analyzer = new BoardAnalyzer(placementRules);
        var currentDetail = analyzer.TryAnalyze(current, hp, samples: 1, cancellation: cancellation);
        var recommendedDetail = analyzer.TryAnalyze(current with { Team = slots.Take(6).ToArray() }, hp, samples: 1,
            cancellation: cancellation);
        return currentDetail is null || recommendedDetail is null ? null :
            new(slots.Select(unit => unit?.Clone()).ToArray(), currentDetail, recommendedDetail)
            {
                ExcludesRandomTrinketEffects = excludesRandom,
                CurrentSurvival = baseline.Survival,
                RecommendedSurvival = recommended.Survival
            };
    }

    private static string UnitIdentity(Unit unit) => JsonSerializer.Serialize(new
    {
        unit.SpeciesId, unit.Level, unit.Shiny, unit.AddedTypes, unit.Bonuses, unit.StoredDiscount, unit.Copies,
        Counters = unit.Counters.OrderBy(pair => pair.Key, StringComparer.Ordinal), unit.CapturedCooldown, unit.Devoured
    });

    private static bool IsRandomTarget(Effect effect) => effect.Target is Target.RandomAlly or Target.RandomEnemy;

    private static bool IsBetter(Score candidate, Score current, Func<Score, double> damage)
    {
        const double epsilon = .01;
        if (candidate.Survival is { } survival && current.Survival is { } baseline)
        {
            if (survival.DefeatedAt is { } defeat)
                return baseline.DefeatedAt is { } previous && defeat > previous + epsilon;
            return baseline.DefeatedAt.HasValue || survival.Remaining > baseline.Remaining + epsilon;
        }
        if (candidate.Kill is { } kill)
        {
            if (current.Kill is not { } baselineKill) return true;
            var order = Math.Round(kill, 2).CompareTo(Math.Round(baselineKill, 2));
            if (order != 0) return order < 0;
        }
        else if (current.Kill.HasValue) return false;
        return (damage(candidate) - damage(current)) / 30 > epsilon;
    }
}
