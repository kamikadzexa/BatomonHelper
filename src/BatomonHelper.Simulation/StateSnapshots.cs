namespace BatomonHelper.Simulation;

public static class StateSnapshots
{
    public static FrozenBoard Board(Observation o)=>new(o.Day,o.MaxHp,o.Trainer,o.Units.Take(6).Select(x=>x?.Clone()).ToArray(),
        [..o.Bag],[..o.ExtraTrainers],o.Counters is null?null:new(o.Counters),o.Rank,o.VisibleState?.ForeignPool is null?null:[..o.VisibleState.ForeignPool],o.Wins);
    public static Observation Copy(Observation o)=>o with {
        Units=o.Units.Select(x=>x?.Clone()).ToArray(),Shop=o.Shop.Select(x=>x is null?null:x with {Creature=x.Creature?.Clone()}).ToArray(),
        Bag=[..o.Bag],ExtraTrainers=[..o.ExtraTrainers],Choices=o.Choices.Select(RunState.CopyChoice).ToArray(),Overflow=o.Overflow?.Clone(),
        Counters=o.Counters is null?null:new(o.Counters),VisibleState=o.VisibleState is not {} v?null:v with {
            ForeignPool=[..v.ForeignPool],PendingUnits=v.PendingUnits.Select(x=>x.Clone()).ToArray(),
            PendingShopEffects=[..v.PendingShopEffects],PendingPurchaseBuffs=[..v.PendingPurchaseBuffs]}};
    public static FrozenBoard Copy(FrozenBoard b)=>b with {Team=b.Team.Select(x=>x?.Clone()).ToArray(),Bag=[..b.Bag],ExtraTrainers=[..b.ExtraTrainers],
        Counters=b.Counters is null?null:new(b.Counters),ForeignPool=b.ForeignPool is null?null:[..b.ForeignPool]};
    public static DayRecord Copy(DayRecord d)=>d with {BeforeBattle=Copy(d.BeforeBattle),Opponent=Copy(d.Opponent),Choices=[..d.Choices],
        Result=d.Result with {PersistentTeam=d.Result.PersistentTeam.Select(x=>x?.Clone()).ToArray(),Bag=[..d.Result.Bag],Trace=[..d.Result.Trace],
            PersistentCounters=d.Result.PersistentCounters is null?null:new(d.Result.PersistentCounters),
            PendingPurchaseBuffs=d.Result.PendingPurchaseBuffs is null?null:[..d.Result.PendingPurchaseBuffs]}};
    public static RunRecord Copy(RunRecord r)=>r with {Days=r.Days.Select(Copy).ToArray()};
}
