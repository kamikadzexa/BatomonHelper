namespace BatomonHelper.Simulation;

/// <summary>Learning feedback, separate from the game's terminal success reward.</summary>
public static class TrainingObjective
{
    public const int Version = 3;
    public const int MaximumShopDecisions = 64;

    public static ulong EvaluationOpponentSeed(ulong baseSeed,int environment,long episode,int day) =>
        baseSeed^((ulong)environment<<48)^((ulong)episode*0x9E3779B97F4A7C15UL)^((ulong)day*0xBF58476D1CE4E5B9UL);

    public static int DeployableBench(IReadOnlyList<Unit?> units)
    {
        var empty=0;for(var i=0;i<Math.Min(6,units.Count);i++)if(units[i] is null)empty++;
        var bench=0;for(var i=6;i<Math.Min(10,units.Count);i++)if(units[i] is not null)bench++;
        return Math.Min(empty,bench);
    }

    public static double DeploymentPotential(IReadOnlyList<Unit?> units)
    {
        var deployed=0;for(var i=0;i<Math.Min(6,units.Count);i++)if(units[i] is not null)deployed++;
        return .04*deployed-.08*DeployableBench(units);
    }

    public static double Reward(double deploymentPotentialBefore, RunState after, StepResult result)
    {
        // Potential differences cancel over an episode (terminal potential is zero).
        // Buying then selling, or moving out and back, cannot farm deployment reward.
        var potentialAfter=result.Terminal?0:DeploymentPotential(after.Units);
        var reward=result.Reward-.002+potentialAfter-deploymentPotentialBefore;
        if (result.DayRecord is { } day)
        {
            reward += day.Result.PlayerWon ? .1 : -.1;
            // Strong guidance, not a hard rule: Finish remains legal.
            reward-=.10*DeployableBench(day.BeforeBattle.Units);
        }
        return reward;
    }

    public static SimAction[] Candidates(GameEnvironment environment, int decisions, int limit)
    {
        var legal = environment.GetLegalActions();
        // Restrict the published candidate set, so PPO still records the chosen action's
        // actual probability. Never replace a sampled action behind the learner's back.
        if (decisions < limit) return legal;
        return environment.State.Phase switch
        {
            Phase.Shop => legal.Where(x => x.Kind == Move.Finish).ToArray(),
            Phase.Overflow => legal.Where(x => x.Source == 10).ToArray(),
            _ => legal
        };
    }
}
