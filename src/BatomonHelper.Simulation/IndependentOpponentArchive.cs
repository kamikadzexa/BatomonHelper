namespace BatomonHelper.Simulation;

public static class IndependentOpponentArchive
{
    public static LeagueEntry[] Build(RulesManifest rules,ulong seed)
    {
        var entries=new List<LeagueEntry>();
        for(var variant=0;variant<4;variant++){
            var env=new GameEnvironment(rules);env.Reset(seed+100000+(ulong)variant,rules.Trainers[variant%rules.Trainers.Length].Id);
            var policy=new StrongestShopPolicy(rules);var random=new SeedStream(seed+900000+(ulong)variant);var turn=0;
            for(var decisions=0;decisions<3000&&env.State.Phase!=Phase.Complete;decisions++){
                var legal=env.GetLegalActions();var move=variant==0?legal[random.Index(legal.Length)]:policy.Choose(env);
                if(env.State.Phase==Phase.Shop&&turn>=64)move=new(Move.Finish);
                if(move.Kind==Move.Finish){var board=FrozenBoard.From(env.State);entries.Add(new(-1,variant,"baseline",board));
                    env.Step(move,b=>entries.Where(x=>x.Board.Day==b.Day).OrderBy(x=>x.Policy).First().Board);turn=0;
                }else{env.Step(move);turn++;}
            }
        }
        // A legal independent run deliberately loses early battles, then wins ties.
        // This covers all normal-run days without borrowing the evaluated player's board.
        for(var attempt=0;attempt<8&&!entries.Any(x=>x.Board.Day==16);attempt++){
            var env=new GameEnvironment(rules);env.Reset(seed+700000+(ulong)attempt,rules.Trainers.FirstOrDefault(x=>x.Id=="youngster-m")?.Id??rules.Trainers[0].Id);
            for(var i=0;i<1000&&env.State.Phase!=Phase.Complete;i++){
                var legal=env.GetLegalActions();
                if(legal.FirstOrDefault(x=>x.Kind==Move.Choose) is {} choice){env.Step(choice);continue;}
                if(legal.FirstOrDefault(x=>x.Kind==Move.Sell) is {} sell){env.Step(sell);continue;}
                var board=FrozenBoard.From(env.State);entries.Add(new(-1,4,"baseline",board));
                env.Step(new(Move.Finish),b=>env.State.Day<=6?entries.Where(x=>x.Board.Day==b.Day).MaxBy(x=>x.Board.Team.OfType<Unit>().Sum(u=>
                    LevelRules.Resolve(rules.Species.First(s=>s.Id==u.SpeciesId),u.Level,u.Shiny).Stats.Take(6).Sum()+u.Bonuses.Take(6).Sum()))!.Board:b);
            }
        }
        return entries.ToArray();
    }
}
