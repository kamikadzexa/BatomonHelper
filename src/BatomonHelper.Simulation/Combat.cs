using BatomonHelper.Core;
using System.Collections.Concurrent;
namespace BatomonHelper.Simulation;

public sealed class Combat(RulesManifest rules)
{
    private const string Balance28Build = "26773903";
    private static readonly int[] ScreenSlots = [0, 1, 2, 3, 4, 5];
    private static readonly int[] PlayerBattleSlots = [5, 4, 3, 2, 1, 0];
    private static readonly int[] OpponentBattleSlots = [3, 4, 5, 0, 1, 2];
    private readonly Dictionary<string, Species> _species = rules.Species.ToDictionary(x => x.Id);
    private readonly Dictionary<string, InventoryDefinition> _trinkets = rules.Trinkets.ToDictionary(x => x.Id);
    private readonly Dictionary<string, InventoryDefinition> _trainers = rules.Trainers.ToDictionary(x => x.Id);
    private readonly bool _hasAmplifiers=rules.Species.Any(s=>s.Normal.Concat(s.Shiny).Any(l=>l.Effects.Any(e=>e.Operation==Operation.RepeatOngoing)));
    private readonly bool _hasCooldownAuras=rules.Species.Any(s=>s.Normal.Concat(s.Shiny).Any(l=>l.Effects.Any(e=>e.Trigger==Trigger.Ongoing && e.Operation==Operation.AddedCooldown)));
    public static double EffectiveCooldown(double basis, double speed, double added = 0) =>
        Math.Max(.1, Math.Round(basis / Math.Max(.1, 1 + speed) + Math.Max(0, added), 1,
            MidpointRounding.AwayFromZero));
    public static double Absorb(double damage, ref double shield, bool status)
    {
        if (status && shield > 0) damage *= .85;
        var absorbed = Math.Min(shield, damage); shield -= absorbed; return damage - absorbed;
    }
    private static readonly Dictionary<(int,Target,bool),int[]> TargetCache=Enumerable.Range(0,6)
        .SelectMany(slot=>Enum.GetValues<Target>().SelectMany(target=>new[]{false,true}.Select(linked=>(slot,target,linked))))
        .ToDictionary(x=>x,x=>BuildTargets(x.slot,x.target,x.linked));
    public static int[] Targets(int slot,Target target,bool linked=false)=>[..TargetSlots(slot,target,linked)];
    private static int[] TargetSlots(int slot,Target target,bool linked=false)=>TargetCache[(slot,target,linked)];
    private readonly ConcurrentDictionary<(string,int,bool),LevelDefinition> _levels=[];
    private readonly ConcurrentDictionary<string,string[]> _matchTypes=[];
    private static readonly string[] StatusNames = ["Burn", "Poison", "Shock"];
    private static readonly Trigger[] StatusTriggers = [Trigger.AllyBurn, Trigger.AllyPoison, Trigger.AllyShock];
    private readonly ConcurrentDictionary<LevelDefinition,Dictionary<Trigger,Effect[]>> _effectGroups=[];
    private Effect[] Effects(LevelDefinition definition,Trigger trigger)=>_effectGroups.GetOrAdd(definition,
        d=>d.Effects.GroupBy(x=>x.Trigger).ToDictionary(x=>x.Key,x=>x.ToArray())).GetValueOrDefault(trigger)??[];
    private static int[] BuildTargets(int slot, Target target, bool linked = false) => target switch
    {
        Target.Self => [slot], Target.Allies => Enumerable.Range(0,6).Where(i => i != slot).ToArray(), Target.Above => slot >= 3 ? [slot-3] : [], Target.Below => slot < 3 ? [slot+3] : [],
        Target.Left => slot % 3 > 0 ? [slot-1] : [], Target.Right => slot % 3 < 2 ? [slot+1] : [],
        Target.Adjacent => Enumerable.Range(0,6).Where(i => i != slot && (linked || i/3 == slot/3 && Math.Abs(i-slot)==1 || Math.Abs(i-slot)==3)).ToArray(),
        Target.Row or Target.EnemyRow or Target.BothRows => Enumerable.Range(slot/3*3,3).ToArray(), Target.Column => [slot%3,slot%3+3], Target.Opposite => [slot], Target.BottomRight => [5],
        Target.TopMiddle => [1], Target.LeftColumn => [0,3], Target.RightColumn => [2,5],
        _ => [0,1,2,3,4,5]
    };
    private static Target ScreenTarget(Target target, bool mirrored) => !mirrored ? target : target switch
    {
        Target.Left => Target.Right,
        Target.Right => Target.Left,
        Target.LeftColumn => Target.RightColumn,
        Target.RightColumn => Target.LeftColumn,
        _ => target
    };

    private (double Flat, double Percent) OngoingHp(FrozenBoard board)
    {
        var flat = 0d;
        var percent = 0d;
        void Add(Effect effect, Unit? unit = null, int count = 1)
        {
            if (effect.Percent) percent += effect.Amount * count;
            else flat += effect.Amount * count;
            if (effect.ProgressCounter is { } key && unit is not null)
                flat += unit.Counters.GetValueOrDefault("ability:" + key) * count;
        }

        foreach (var entry in board.Bag)
            foreach (var effect in _trinkets[entry.Id].Effects)
                if (effect is { Operation: Operation.TeamHp, Trigger: Trigger.Ongoing }) Add(effect, count: entry.Quantity);

        var linked = board.Bag.Any(entry => _trinkets[entry.Id].Effects.Any(effect => effect.Operation == Operation.LinkAllies));
        var definitions = board.Team.Select(unit => unit is null ? null : Level(unit)).ToArray();
        for (var slot = 0; slot < board.Team.Length; slot++)
            if (board.Team[slot] is { } unit)
                foreach (var effect in definitions[slot]!.Effects)
                    if (effect is { Operation: Operation.TeamHp, Trigger: Trigger.Ongoing })
                    {
                        var count = 1;
                        for (var actor = 0; actor < definitions.Length; actor++)
                            if (definitions[actor] is { } definition)
                                foreach (var amplifier in definition.Effects)
                                    if (amplifier is { Operation: Operation.RepeatOngoing, Trigger: Trigger.Ongoing } &&
                                        TargetSlots(actor, ScreenTarget(amplifier.Target, board.Mirrored), linked).Contains(slot))
                                        count += (int)amplifier.Amount;
                        Add(effect, unit, count);
                    }
        return (flat, percent);
    }

    private LevelDefinition Level(Unit unit) => _levels.GetOrAdd((unit.SpeciesId, unit.Level, unit.Shiny),
        key => LevelRules.Resolve(_species[key.Item1], key.Item2, key.Item3));

    internal double RecoverBaseHp(FrozenBoard board)
    {
        var (flat, percent) = OngoingHp(board);
        return board.MaxHp / (1 + percent) - flat;
    }

    public BattleResult ResolveBattle(FrozenBoard player, FrozenBoard opponent, ulong seed, bool trace = false, bool reference = false,bool cacheStats=true, CombatProbe? probe=null, Action<GameBattleFrame>? audit=null)
    {
        rules.RequireCombatReady();
        if (player.Day != opponent.Day) throw new ArgumentException("Opponent must be from the same day.");
        var rng = new SeedStream(seed);
        var teams = new[] { new Side(player), new Side(opponent) };
        int[] SideTargets(int side, int slot, Target target, bool linked = false) =>
            teams[side].Mirrored && target == Target.BottomRight ? [3]
                : TargetSlots(slot, ScreenTarget(target, teams[side].Mirrored), linked);
        InventorySummary InventoryState(int side) => teams[side].Inventory ??= new(teams[side].Bag, _trinkets);
        var statCache=new Dictionary<(int,int,UInt128),double[]>();
        void Invalidate()=>statCache.Clear();
        var queue = new PriorityQueue<(int Side,int Slot,long Epoch,int Remaining), (long Tick,long Order)>();
        var log = new List<string>(); long order = 0; long now = 0; long operations = 0;
        var balance28 = rules.Build == Balance28Build;
        var tickSeconds = balance28 ? 1d / 30 : rules.Profile?.CombatTickSeconds ?? .01;
        int[] BattleSlots(int side) => !balance28 ? ScreenSlots : side == 0 ? PlayerBattleSlots : OpponentBattleSlots;
        if(probe is not null&&(!double.IsFinite(probe.Duration)||probe.Duration<tickSeconds||probe.Duration>120))throw new ArgumentOutOfRangeException(nameof(probe),"Analysis duration must be between one tick and 120 seconds.");
        long Tick(double seconds) => (long)Math.Round(seconds/tickSeconds,MidpointRounding.AwayFromZero);
        // The Balance 28 captures fit four game ticks between consecutive casts by one unit.
        var multicastSpacing = balance28 ? 4 : Tick(.1);
        var scoreLimit = probe is not null ? Tick(probe.Duration) : long.MaxValue;
        var pressureTick = probe?.PressureHp is not null ? Tick(1) : long.MaxValue;
        void Observe()
        {
            if (audit is null) return;
            audit(new(now * tickSeconds, ObserveSide(0), ObserveSide(1)));
        }
        GameBattleFrameSide ObserveSide(int side)
        {
            var team = teams[side];
            return new(Math.Max(0, team.Hp), team.MaxHp, team.Shield, (long)team.Status[0], (long)team.Status[1], (long)team.Status[2],
                team.Units.Select((unit, slot) => (unit, slot)).Where(entry => entry.unit is not null)
                    .Select(entry => new GameBattleFrameUnit(entry.slot, entry.unit!.Alive, entry.unit.Casts,
                        Stats(side, entry.slot).ToArray(), entry.unit.Damage, entry.unit.Healing, entry.unit.ShieldGranted,
                        Cooldown(side, entry.slot), BasisCooldown(entry.unit) is null ? null : entry.unit.Progress,
                        balance28 ? entry.unit.PendingCasts : null)).ToArray(), team.Hp);
        }
        void Output(int side,string kind,double amount)
        {
            if(side==0 && probe is { ScoreOnly: false } && amount>0)probe.Output.Add(new(now*tickSeconds,kind,amount));
        }
        void Damage(int side,double amount,string kind,bool status)
        {
            var dealt=Absorb(amount,ref teams[side].Shield,status);teams[side].Hp-=dealt;
            if (side != 1 || probe is null || dealt <= 0) return;
            if (probe.ScoreOnly)
            {
                probe.TotalDamage += dealt;
                if (probe.KillSeconds is null && probe.KillTargetHp is { } target && probe.TotalDamage >= target)
                    probe.KillSeconds = now * tickSeconds;
            }
            else probe.Damage.Add(new(now*tickSeconds,kind,dealt));
        }
        void Snapshot()
        {
            if(probe is null || probe.ScoreOnly)return;
            if(probe.StopQuietlyOnCancellation)
            {
                if(probe.Cancellation.IsCancellationRequested)return;
            }
            else probe.Cancellation.ThrowIfCancellationRequested();
            var units=new List<CombatUnitSnapshot>();
            for(var slot=0;slot<6;slot++)if(teams[0].Units[slot] is {} u){
                var stats=Stats(0,slot).ToArray();
                var counters=new Dictionary<string,double>(u.Unit.Counters);
                foreach(var pair in u.Activations)counters["effect:"+pair.Key]=pair.Value;
                foreach(var pair in u.Growth)counters[$"growth:{pair.Key.Trigger}/{pair.Key.Operation}/{pair.Key.Target}/{pair.Key.Stat}"]=pair.Value;
                units.Add(new(slot,u.Unit.SpeciesId,u.Alive,stats,Cooldown(0,slot),counters));
            }
            double CastRate(int slot)
            {
                if(teams[0].Units[slot] is not {Alive:true,Disabled:false} unit)return 0;
                var repeats=(int)Stats(0,slot)[(int)Stat.Multicast];
                var rate = unit.ParentSlot is { } parent ? repeats * CastRate(parent)
                    : Cooldown(0, slot) is { } cooldown ? repeats / cooldown : 0;
                return balance28 ? Math.Min(rate, 1 / (multicastSpacing * tickSeconds)) : rate;
            }
            var outputRates=new Dictionary<string,double>{{"Burn",0},{"Poison",0},{"Shock",0},{"Heal",0},{"Shield",0}};
            foreach(var unit in units){
                var rate=CastRate(unit.Slot);
                foreach(var (kind,stat) in new[]{("Burn",Stat.Burn),("Poison",Stat.Poison),("Shock",Stat.Shock),("Heal",Stat.Heal),("Shield",Stat.Shield)}){
                    var multiplier=kind=="Heal"?1+.25*BagCount(0,"sapphire-amulet"):kind=="Shield"?1+.25*BagCount(0,"topaz-amulet"):1;
                    outputRates[kind]+=Math.Max(0,unit.Stats[(int)stat])*rate*multiplier;
                }
            }
            probe.Frames.Add(new(now*tickSeconds,[..teams[1].Status],units.ToArray()){OutputRates=outputRates});
        }
        for (var side=0; side<2; side++)
        {
            teams[side].MaxHp = side == 0 ? player.MaxHp : opponent.MaxHp;
            teams[side].Hp = teams[side].MaxHp;
            var board = side == 0 ? player : opponent;
            if (!board.IncludesOngoingHp)
            {
                var (flat, percent) = OngoingHp(board);
                teams[side].MaxHp = (teams[side].MaxHp + flat) * (1 + percent);
                teams[side].Hp = teams[side].MaxHp;
            }
            foreach (var unit in teams[side].Units)
                if (unit is not null && teams[side].Trainers.Contains("pyromaniac") && Types(unit).Count() == 1)
                {
                    unit.Unit.AddedTypes = unit.Unit.AddedTypes.Append("fire").Distinct().ToArray();
                    unit.PersistentUnit.AddedTypes = [..unit.Unit.AddedTypes];
                }
        }
        for(var side=0;side<2;side++)
        {
            Inventory(side,1,Trigger.BattleStart);
            var startRepeats=(int)teams[side].Counters.GetValueOrDefault("next-battle-start-repeat")+
                teams[side].Bag.Sum(entry=>entry.Quantity*_trinkets[entry.Id].Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.NextBattleStartRepeat).Sum(x=>(int)x.Amount));
            foreach (var slot in BattleSlots(side))
                if (teams[side].Units[slot] is { } unit)
                    for(var repeat=0;repeat<=startRepeats;repeat++)Activate(side,slot,Trigger.BattleStart);
        }
        long nextBurn=Tick(.5),nextPoison=Tick(1),nextStorm=probe is null?Tick(30.5):long.MaxValue;
        Observe();
        Snapshot();long nextSnapshot=probe is { ScoreOnly: true } ? long.MaxValue : Tick(.5);
        var casts = new List<(int Side, int Slot, double[] Stats)>();
        while (true)
        {
            if(probe is {StopQuietlyOnCancellation:true} && probe.Cancellation.IsCancellationRequested)break;
            if (++operations > 1_000_000) throw new InvalidOperationException("Battle event limit exceeded; preserve the seed for a rules audit.");
            var next = now+1;
            if (!reference)
            {
                next = Math.Min(pressureTick, Math.Min(nextBurn, Math.Min(nextPoison,nextStorm)));
                if (queue.TryPeek(out _,out var priority)) next=Math.Min(next,priority.Tick);
                for(var side=0;side<2;side++) for(var slot=0;slot<6;slot++)
                    if(teams[side].Units[slot] is { Alive:true, ParentSlot:null } u && Cooldown(side, slot) is { } cooldown)
                    {
                        var remaining=cooldown-u.Progress;
                        next=Math.Min(next,now+Math.Max(1,(long)Math.Ceiling((remaining-1e-9)/tickSeconds)));
                    }
                for(var side=0;side<2;side++)for(var slot=0;slot<6;slot++)if(teams[side].Units[slot] is {Alive:true,Disabled:false} u)
                    foreach(var effect in Effects(Definition(u),Trigger.Timer))
                        next=Math.Min(next,now+Math.Max(1,(long)Math.Ceiling((effect.IntervalSeconds-u.Timers.GetValueOrDefault(effect)-1e-9)/tickSeconds)));
                next=Math.Max(now+1,next);
            }
            if(probe is not null)next=Math.Min(next,Math.Min(nextSnapshot,Tick(probe.Duration)));
            var elapsed=(next-now)*tickSeconds;
            foreach(var team in teams) foreach(var u in team.Units) if(u is { Alive:true })
            {
                u.Progress+=elapsed;
                if(!u.Disabled)foreach(var effect in Effects(Definition(u),Trigger.Timer))u.Timers[effect]=u.Timers.GetValueOrDefault(effect)+elapsed;
            }
            now=next;
            for(var side=0;side<2;side++)foreach(var slot in BattleSlots(side))if(teams[side].Units[slot] is {Alive:true,Disabled:false} u)
                foreach(var effect in Effects(Definition(u),Trigger.Timer))if(u.Timers.GetValueOrDefault(effect)+1e-9>=effect.IntervalSeconds)
                {u.Timers[effect]=0;for(var repeat=0;repeat<=OngoingRepeats(side,slot);repeat++)Apply(side,slot,effect);}
            for(var side=0;side<2;side++) foreach(var slot in BattleSlots(side))
                if(teams[side].Units[slot] is { Alive:true, ParentSlot:null } u && Cooldown(side, slot) is { } cooldown &&
                    u.Progress+1e-9>=cooldown)
                { u.Progress=0; Sequence(side,slot); }
            casts.Clear();
            while(queue.TryPeek(out var casting,out var at) && at.Tick<=now)
            {
                if (probe is { StopQuietlyOnCancellation: true } && probe.Cancellation.IsCancellationRequested) break;
                queue.Dequeue();
                if(teams[casting.Side].Units[casting.Slot] is not { Alive:true } caster || caster.Epoch!=casting.Epoch) continue;
                if (balance28)
                {
                    caster.NextCastTick = now + multicastSpacing;
                    if (--caster.PendingCasts > 0 && caster.NextCastTick <= scoreLimit)
                        queue.Enqueue(casting, (caster.NextCastTick, at.Order));
                }
                else if (casting.Remaining > 1)
                    queue.Enqueue((casting.Side, casting.Slot, casting.Epoch, casting.Remaining - 1),
                        (at.Tick + multicastSpacing, at.Order + 1));
                // The game reads output stats before on_cast runs. Self buffs such as
                // Mosslug's damage growth therefore affect the next cast, not this one.
                var castStats=(double[])Stats(casting.Side,casting.Slot).Clone();
                if (audit is not null) caster.Casts++;
                Activate(casting.Side,casting.Slot,Trigger.Cast);
                foreach(var child in BattleSlots(casting.Side))if(teams[casting.Side].Units[child] is {Alive:true} summoned && summoned.ParentSlot==casting.Slot)
                    Sequence(casting.Side,child,casting.Slot);
                Inventory(casting.Side,casting.Slot,Trigger.Cast);
                casts.Add((casting.Side,casting.Slot,castStats));
                NotifyAllies(casting.Side,casting.Slot,Trigger.AllyCast);
                if(trace) log.Add(FormattableString.Invariant($"{now*tickSeconds:F2}s side {casting.Side} slot {casting.Slot}: {caster.Unit.SpeciesId} cast"));
            }
            // All casts on this tick share the published Shield -> Damage -> Status -> Heal phases.
            foreach(var cast in casts)
            {var shield=Math.Max(0,cast.Stats[(int)Stat.Shield])*(1+.25*BagCount(cast.Side,"topaz-amulet"));teams[cast.Side].Shield+=shield;Output(cast.Side,"Shield",shield);if(cast.Stats[(int)Stat.Shield]>0)
                {NotifyAllies(cast.Side,cast.Slot,Trigger.AllyShield,cast.Stats);NotifyAllies(cast.Side,cast.Slot,Trigger.TeamShield,cast.Stats,true);}
                if (audit is not null && teams[cast.Side].Units[cast.Slot] is { } caster) caster.ShieldGranted += shield;
            }
            foreach(var cast in casts)
            {
                var enemy=teams[1-cast.Side]; var direct=Math.Max(0,cast.Stats[(int)Stat.Damage]);
                if(direct>0) { Damage(1-cast.Side,enemy.Status[2],"Shock",true); Damage(1-cast.Side,direct,"Direct",false);
                    if (audit is not null && teams[cast.Side].Units[cast.Slot] is { } caster) caster.Damage += direct;
                    NotifyAllies(cast.Side,cast.Slot,Trigger.TeamDamage,cast.Stats,true);}
            }
            foreach(var cast in casts) for(var status=0;status<3;status++)
            {
                var amount = Math.Max(0, cast.Stats[(int)Stat.Burn + status]);
                if (amount == 0) continue;
                teams[1-cast.Side].Status[status] += amount;
                Invalidate();
                Output(cast.Side,StatusNames[status],amount);
                if(amount>0) {NotifyAllies(cast.Side,cast.Slot,StatusTriggers[status],cast.Stats);
                    if(status==0)NotifyAllies(cast.Side,cast.Slot,Trigger.TeamBurn,cast.Stats,true);}
            }
            if (now >= pressureTick && probe?.PressureHp is { } pressure)
            {
                Damage(0, teams[0].Status[2], "Shock", true);
                Damage(0, pressure * .05, "Direct", false);
                var seconds = (int)Math.Round(now * tickSeconds);
                if (seconds % 5 == 0 && seconds <= 25)
                {
                    teams[0].Status[0] += Math.Ceiling(pressure * .005);
                    teams[0].Status[1] += Math.Ceiling(pressure * .0025);
                    teams[0].Status[2] += Math.Ceiling(pressure * .01);
                    Invalidate();
                }
                pressureTick += Tick(1);
            }
            foreach(var cast in casts) {
                var healing=Math.Max(0,cast.Stats[(int)Stat.Heal])*(1+.25*BagCount(cast.Side,"sapphire-amulet"));
                Output(cast.Side,"Heal",healing);
                if (audit is not null && teams[cast.Side].Units[cast.Slot] is { } caster) caster.Healing += healing;
                teams[cast.Side].Hp=Math.Min(teams[cast.Side].MaxHp,teams[cast.Side].Hp+healing);
            }
            if(now>=nextBurn)
            {
                for (var side = 0; side < 2; side++)
                {
                    if (teams[side].Status[0] <= 0) continue;
                    Damage(side, teams[side].Status[0], "Burn", true);
                    teams[side].Status[0] = Math.Max(0, teams[side].Status[0] - 1);
                    Invalidate();
                }
                nextBurn+=Tick(.5);
            }
            if(now>=nextPoison) { for(var side=0;side<2;side++)Damage(side,teams[side].Status[1],"Poison",true); nextPoison+=Tick(1); }
            if(probe is null && now>=nextStorm)
            {
                var damage=8*Math.Pow(2,(now*tickSeconds-30)/3);
                foreach(var team in teams) team.Hp-=Absorb(damage,ref team.Shield,false);
                nextStorm+=Tick(.5);
            }
            Observe();
            if (probe is { StopAtTarget: true, KillSeconds: not null }) break;
            if (probe?.PressureHp is not null && teams[0].Hp <= 0)
            {
                probe.Survival = new(0, 0, now*tickSeconds);
                break;
            }
            if(probe is not null){
                if(now>=nextSnapshot){Snapshot();nextSnapshot+=Tick(.5);}
                if(now>=Tick(probe.Duration))break;
            }else if(teams.Any(x=>x.Hp<=0)) break;
        }
        if (probe?.PressureHp is not null && probe.Survival is null)
            probe.Survival = new(teams[0].Hp, teams[0].Shield, null);
        if (probe is not null) probe.UsedRandomness = rng.State != seed;
        var won=teams[1].Hp<=0;
        if(probe is null){
            for(var slot=0;slot<6;slot++) if(teams[0].Units[slot] is not null) Activate(0,slot,won?Trigger.Victory:Trigger.Defeat);
            Inventory(0,1,won?Trigger.Victory:Trigger.Defeat);
        }
        return new(won,teams.All(x=>x.Hp<=0),now*tickSeconds,teams[0].Hp,teams[1].Hp,
            teams[0].Units.Select(x=>x is {TemporarySpawn:false}?x.PersistentUnit.Clone():null).ToArray(),[..teams[0].Bag],teams[0].Gold,[..log],new(teams[0].Counters),[..teams[0].PendingPurchaseBuffs]);

        LevelDefinition Definition(Fighter u) => Level(u.Unit);
        double? BasisCooldown(Fighter fighter) => fighter.Unit.CapturedCooldown is { } live &&
            (live.SpeciesId, live.Level, live.Shiny) == (fighter.Unit.SpeciesId, fighter.Unit.Level, fighter.Unit.Shiny)
            ? live.Seconds : Definition(fighter).Cooldown;
        double? Cooldown(int side, int slot) => BasisCooldown(teams[side].Units[slot]!) is { } basis
            ? EffectiveCooldown(basis, Stats(side, slot)[(int)Stat.Speed], AddedCooldown(side, slot)) : null;
        double AddedCooldown(int side, int slot) => teams[side].Units[slot]!.AddedCooldown + OngoingCooldown(side, slot);
        void Charge(int side, int slot, Fighter unit, double seconds)
        {
            if (unit.Unit.SpeciesId is "dracana" or "ironcore" or "steamscuttle") return;
            if (!balance28) unit.Progress += seconds;
            else if (Cooldown(side, slot) is { } cooldown)
                unit.Progress += Math.Min(Math.Max(cooldown - unit.Progress, 0), seconds);
        }
        bool Matches(Fighter u,Effect effect) => (effect.Operation==Operation.AddType || effect.Type is null || _matchTypes.GetOrAdd(effect.Type,text=>text.Split('|')).Any(type=>Types(u).Contains(type))) &&
            u.Unit.SpeciesId!=effect.ExcludeSpecies && (effect.Rarity is null || _species[u.Unit.SpeciesId].Rarity==effect.Rarity) &&
            (effect.MinimumLevel is null || u.Unit.Level>=effect.MinimumLevel) && (effect.MaximumLevel is null || u.Unit.Level<=effect.MaximumLevel) &&
            (!effect.RequireNoAbility||Definition(u).Effects.Length==0) && (!effect.RequireOngoing||Definition(u).AbilityTrigger==Trigger.Ongoing || Definition(u).Effects.Any(x=>x.Trigger==Trigger.Ongoing));
        double[] Base(Fighter u){var stats=Definition(u).Stats;var result=new double[9];
            for(var i=0;i<9;i++){var value=stats[i]+u.Unit.Bonuses[i]+u.Temporary[i];result[i]=i==(int)Stat.Speed?value:Math.Max(0,value);}return result;}
        UInt128 StatBit(int side,int slot,Stat stat) => (UInt128)1<<((side*6+slot)*9+(int)stat);
        double Scale(int side,int slot,Effect effect,UInt128 excludedDynamicStats=default,double[]? eventStats=null) => effect.Scale==EffectScale.None?1:EffectScaling.Factor(effect,teams[side].Units[slot]?.Unit,
            teams[side].Units.Select((x,i)=>(x,i)).Where(x=>x.x is {Alive:true} && (effect.Scale!=EffectScale.AlliedStat ||
                x.i!=slot && x.x.Unit.SpeciesId!=effect.ScaleExcludeSpecies && SideTargets(side,slot,effect.ScaleTarget,Linked(side)).Contains(x.i))).Select(x=>x.x!.Unit),teams[side].Bag,_species,
            unit=>ScaleStats(side,unit,effect.Trigger==Trigger.Ongoing?excludedDynamicStats | StatBit(side,slot,effect.Stat):excludedDynamicStats),
            teams[1-side].Status,teams[side].Counters.GetValueOrDefault("lives-lost"),_trinkets,eventStats,teams[side].Wins,
            teams[side].Counters.GetValueOrDefault("items-used-rarity-"+effect.ScaleRarity));
        double[] ScaleStats(int side,Unit unit,UInt128 excludedDynamicStats)
        {
            var values=(double[])Stats(side,Array.FindIndex(teams[side].Units,x=>ReferenceEquals(x?.Unit,unit)),excludedDynamicStats).Clone();
            values[(int)Stat.SellValue]+=Math.Ceiling((_species[unit.SpeciesId].Cost*(unit.Level switch {1=>1,2=>3,_=>6})+unit.StoredDiscount)/2);
            return values;
        }
        double[] Stats(int side,int slot,UInt128 excludedDynamicStats=default)
        {
            if(cacheStats&&!reference&&statCache.TryGetValue((side,slot,excludedDynamicStats),out var cached))return cached;
            var source=teams[side].Units[slot]!; var values=Base(source);var percentages=System.Buffers.ArrayPool<double>.Shared.Rent(9);
            Array.Clear(percentages,0,9);
            try{
            source.TemporaryPercentages?.CopyTo(percentages, 0);
            void Bonus(Effect e,int actor)
            {
                if(e.Scale!=EffectScale.None && (excludedDynamicStats & StatBit(side,actor,e.Stat))!=0)return;
                var amount=e.Amount*Scale(side,actor,e,excludedDynamicStats);
                if(e.Operation==Operation.Add)values[(int)e.Stat]+=amount;
                if(e.Operation==Operation.Multiply)percentages[(int)e.Stat]+=amount;
            }
            for(var actor=0;actor<6;actor++)
                if(teams[side].Units[actor] is { Alive:true, Disabled:false } u)
                    foreach(var e in Effects(Definition(u),Trigger.Ongoing))
                        if((e.IncludeSelf && actor==slot || SideTargets(side,actor,e.Target,Linked(side)).Contains(slot)) &&
                            (e.IncludeSelf && actor==slot || Matches(source,e)))
                        for(var repeat=0;repeat<=OngoingRepeats(side,actor);repeat++)Bonus(e,actor);
            foreach (var (effect, count) in InventoryState(side).Effects(Trigger.Ongoing))
                if (effect.Operation is Operation.Add or Operation.Multiply && Matches(source, effect) &&
                    SideTargets(side, 1, effect.Target).Contains(slot))
                    for (var copy = 0; copy < count; copy++) Bonus(effect, slot);
            foreach(var trainer in teams[side].Trainers)
                foreach(var e in _trainers[trainer].Effects.Where(x=>x.Trigger==Trigger.Ongoing))
                    if(Matches(source,e))Bonus(e,slot);
            var types=Types(source).ToHashSet();
            var kaleidoscope=BagCount(side,"kaleidoscope");
            if(kaleidoscope>0&&!teams[side].Units.Where((u,i)=>i!=slot && u is {Alive:true}).OfType<Fighter>().Any(u=>Types(u).Any(types.Contains)))values[6]+=.35*kaleidoscope;
            foreach(var trainer in teams[side].Trainers){
                // Musician counts the active roster, including knocked-out units, as the
                // game does in run_data.team. A self-knockout must neither remove its
                // own Shock bonus nor unlock a bonus when two Electrics were fielded.
                if(trainer=="musician" && types.Contains("electric") && teams[side].Units.OfType<Fighter>().Count(u=>Types(u).Contains("electric"))==1)percentages[5]+=1.5;
                if(trainer=="chemist" && types.Contains("toxic"))values[4]+=3+teams[side].Counters.GetValueOrDefault("chemist-levels");
                if(trainer=="pyromaniac" && types.Contains("fire"))values[3]+=2;
            }
            var smallClubs = BagCount(side, "small-club");
            var giantClubs = BagCount(side, "giant-club");
            if (smallClubs + giantClubs > 0 && BasisCooldown(source) is { } basis)
            {
                var speed = values[(int)Stat.Speed] * (1 + percentages[(int)Stat.Speed]);
                var cooldown = balance28 ? EffectiveCooldown(basis, speed, AddedCooldown(side, slot)) : basis;
                if (cooldown >= 5)
                {
                    percentages[(int)Stat.Damage] += .2 * smallClubs;
                    for (var stat = 0; stat < 6; stat++) percentages[stat] += .7 * giantClubs;
                }
            }
            for(var i=0;i<9;i++)values[i]*=1+percentages[i];
            for(var i=0;i<6;i++) values[i]=Math.Clamp(Math.Round(values[i],MidpointRounding.AwayFromZero),0,999_999_999);
            values[0]+=source.DamageAdjustment;
            values[(int)Stat.Multicast]=Math.Max(1,values[(int)Stat.Multicast]);
            if(source.Unit.SpeciesId=="zephyrex")values[(int)Stat.Multicast]=1;
            if(cacheStats&&!reference)statCache[(side,slot,excludedDynamicStats)]=values;
            return values;
            }finally{System.Buffers.ArrayPool<double>.Shared.Return(percentages);}
        }
        void Sequence(int side,int slot,int? parent=null)
        {
            var caster = teams[side].Units[slot]!;
            if(caster.ParentSlot is { } required && required!=parent)return;
            var repeats=(int)Stats(side,slot)[(int)Stat.Multicast];
            if (balance28)
            {
                var nextCast = Math.Max(now, caster.NextCastTick);
                if (nextCast > scoreLimit || repeats <= 0) return;
                if (caster.PendingCasts == 0)
                    queue.Enqueue((side, slot, caster.Epoch, 0), (nextCast, order++));
                caster.PendingCasts += repeats;
                return;
            }
            var scheduled = (int)Math.Min(repeats, (scoreLimit - now) / multicastSpacing + 1);
            if (scheduled <= 0) return;
            // Reserve tie-break order for the entire sequence and queue only its next cast.
            queue.Enqueue((side, slot, caster.Epoch, scheduled), (now, order));
            order += scheduled;
        }
        bool Linked(int side) => InventoryState(side).Linked;
        int OngoingRepeats(int side,int slot)
        {
            if(!_hasAmplifiers)return 0;
            var extra=0;
            for(var actor=0;actor<6;actor++)if(teams[side].Units[actor] is {Alive:true,Disabled:false} u)
                foreach(var e in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.RepeatOngoing))
                    if(SideTargets(side,actor,e.Target,Linked(side)).Contains(slot))extra+=(int)e.Amount;
            return extra;
        }
        double OngoingCooldown(int side,int slot)
        {
            if(!_hasCooldownAuras)return 0;
            var delta=0d;
            for(var owner=0;owner<2;owner++)for(var actor=0;actor<6;actor++)if(teams[owner].Units[actor] is {Alive:true,Disabled:false} u)
                foreach(var e in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.AddedCooldown))
                    if((e.Target==Target.BothRows || owner==side && e.Target!=Target.EnemyRow || owner!=side && e.Target==Target.EnemyRow) &&
                        SideTargets(owner,actor,e.Target,Linked(owner)).Contains(slot))delta+=e.Amount*(1+OngoingRepeats(owner,actor));
            return delta;
        }
        void NotifyAllies(int side,int actor,Trigger trigger,double[]? eventStats=null,bool includeSelf=false)
        {
            foreach(var slot in BattleSlots(side))if((includeSelf || slot!=actor) && teams[side].Units[slot] is { Alive:true,Disabled:false } u)
                React(side,slot,Effects(Definition(u),trigger).Where(x=>EventMatches(side,slot,side,actor,x) &&
                    (x.EventType is null||_species[teams[side].Units[actor]!.Unit.SpeciesId].Types.Concat(teams[side].Units[actor]!.Unit.AddedTypes).Contains(x.EventType))).ToArray(),eventStats,actor);
        }
        bool EventMatches(int watcherSide,int watcherSlot,int actorSide,int actorSlot,Effect e)
        {
            var actor=teams[actorSide].Units[actorSlot]!.Unit;
            return actor.SpeciesId!=e.EventExcludeSpecies && (e.EventMinimumLevel is null || actor.Level>=e.EventMinimumLevel) &&
                (!e.EventAdjacent || actorSide==watcherSide && SideTargets(watcherSide,watcherSlot,Target.Adjacent,Linked(watcherSide)).Contains(actorSlot));
        }
        void NotifyKnockout(int actorSide,int actorSlot,double[] before)
        {
            for(var side=0;side<2;side++)foreach(var slot in BattleSlots(side))
                if((side!=actorSide || slot!=actorSlot) && teams[side].Units[slot] is {Alive:true,Disabled:false} u)
                    React(side,slot,Definition(u).Effects.Where(x=>(x.Trigger==Trigger.AnyKnockout || side==actorSide && x.Trigger==Trigger.AllyKnockout) && EventMatches(side,slot,actorSide,actorSlot,x)).ToArray(),before);
        }
        void React(int side,int slot,Effect[] effects,double[]? eventStats,int actor=-1)
        {
            if(effects.Length==0)return;
            var source=teams[side].Units[slot]!;var origin=source.Unit.SpeciesId;
            var repeats=Definition(source).AbilityTrigger==Trigger.Ongoing?OngoingRepeats(side,slot):0;
            foreach(var effect in effects)for(var repeat=0;repeat<=repeats;repeat++)
            {if(source.Unit.SpeciesId!=origin)return;Apply(side,slot,effect,eventStats,actor);}
        }
        void Activate(int side,int slot,Trigger trigger)
        {
            if(teams[side].Units[slot] is not { } source || source.Disabled || (!source.Alive && trigger!=Trigger.Knockout && trigger!=Trigger.Victory && trigger!=Trigger.Defeat)) return;
            var origin=source.Unit.SpeciesId;var definition=Definition(source);
            var repeats=definition.AbilityTrigger==Trigger.Ongoing?OngoingRepeats(side,slot):0;
            foreach(var effect in Effects(definition,trigger))
                for(var repeat=0;repeat<=repeats;repeat++)
                {if(origin!=source.Unit.SpeciesId)return;Apply(side,slot,effect);}
        }
        void Inventory(int side,int slot,Trigger trigger)
        {
            foreach (var (effect, count) in InventoryState(side).Effects(trigger))
                for (var copy = 0; copy < count; copy++) Apply(side, slot, effect);
            foreach(var trainer in teams[side].Trainers)
                foreach(var effect in _trainers[trainer].Effects.Where(x=>x.Trigger==trigger || trainer=="black-belt" && trigger is Trigger.Victory or Trigger.Defeat))Apply(side,slot,effect);
        }
        void Apply(int side,int slot,Effect effect,double[]? eventStats=null,int eventSlot=-1)
        {
            var affectsStats = effect.Operation != Operation.Charge;
            if (affectsStats) Invalidate();
            try{
            if(++operations>1_000_000) throw new InvalidOperationException("Recursive trigger limit exceeded.");
            if(effect.Operation is Operation.InventoryRule or Operation.TrainerRule){Special(side,slot,effect.DefinitionId!);return;}
            if(effect.Target==Target.BothRows)
            {Apply(side,slot,effect with {Target=Target.Row},eventStats);Apply(side,slot,effect with {Target=Target.EnemyRow},eventStats);return;}
            if(effect.Target==Target.BothTeams)
            {Apply(side,slot,effect with {Target=Target.All},eventStats);Apply(side,slot,effect with {Target=Target.Enemies},eventStats);return;}
            if(effect.OncePerBattle && (teams[side].Units[slot] is not { } owner || !owner.ConsumedEffects.Add(effect)))return;
            if(probe is not null && side==0 && teams[side].Units[slot] is {} observed){
                var key=$"{effect.Trigger}/{effect.Operation}/{effect.Target}/{effect.Stat}";
                observed.Activations[key]=observed.Activations.GetValueOrDefault(key)+1;
            }
            if(effect.GrowthPerTrigger!=0 && teams[side].Units[slot] is { } grower)
            {
                var growth=grower.Growth.GetValueOrDefault(effect);grower.Growth[effect]=growth+effect.GrowthPerTrigger;
                effect=effect with {Amount=effect.Amount+growth};
            }
            if(effect.Scale!=EffectScale.None)
            {
                effect=effect with {Amount=effect.Amount*Scale(side,slot,effect,eventStats:eventStats),Scale=EffectScale.None};
                if(effect.Operation==Operation.GrantTrinket && effect.Amount==0)return;
            }
            if(effect.Operation==Operation.PendingPurchase)
            {
                var source=teams[side].Units[slot]??throw new InvalidOperationException("A contingent purchase buff needs a source creature.");
                // Gemwing can activate Bought effects in combat. Their Type/EventType
                // constrain the future purchase, not the source's current combat type.
                if(!source.TemporarySpawn)teams[side].PendingPurchaseBuffs.Add(new(source.PersistentUnit.Instance,effect));
                return;
            }
            if(effect.Operation==Operation.ForceBerryItems)
            {
                teams[side].Counters["forced-berries"]=teams[side].Counters.GetValueOrDefault("forced-berries")+effect.Amount;
                return;
            }
            if (effect is { Operation: Operation.Charge, Target: Target.Self } &&
                teams[side].Units[slot] is { Alive: true } charged)
            {
                if (effect.IncludeSelf || Matches(charged, effect)) Charge(side, slot, charged, effect.Amount);
                return;
            }
            var enemy=effect.Target is Target.Opposite or Target.Enemies or Target.RandomEnemy or Target.EnemyRow;
            var recipientSide=enemy?1-side:side; var team=teams[recipientSide];
            var slots=(effect.Target==Target.Actor?(eventSlot>=0?[eventSlot]:throw new InvalidOperationException("Actor target requires an event.")):SideTargets(side,slot,effect.Target,Linked(side)))
                .Concat(effect.IncludeSelf?[slot]:[]).Distinct()
                .Where(i=>team.Units[i] is { } u && (u.Alive||effect.Operation==Operation.Revive || effect.ReviveTargets || effect.Trigger is Trigger.Victory or Trigger.Defeat || effect.Trigger==Trigger.Knockout && effect.Target==Target.Self ||
                    effect.Operation==Operation.ActivateAbility && effect.SubjectTrigger is Trigger.Victory or Trigger.Defeat) &&
                    (effect.IncludeSelf && i==slot || Matches(u,effect))).ToArray();
            if(effect.Target is Target.RandomAlly or Target.RandomEnemy && slots.Length>0) slots=[slots[rng.Index(slots.Length)]];
            if(effect.Operation==Operation.Gold) { teams[side].Gold+=effect.Amount; return; }
            if(effect.Operation==Operation.SpawnDevoured)
            {
                if(teams[side].Units[slot]?.Unit.Devoured is not { } devoured)return;
                for(var child=3;child<6;child++)if(teams[side].Units[child] is null)
                {
                    var copy=devoured.Clone();copy.Instance=teams[side].NextUnitInstance++;
                    teams[side].Units[child]=new(copy){ParentSlot=slot,TemporarySpawn=true};
                }
                return;
            }
            if(effect.Operation==Operation.CopyEnemyStats)
            {
                var candidates=Enumerable.Range(0,6).Where(i=>teams[1-side].Units[i] is {Alive:true}).Select(i=>Stats(1-side,i)).ToArray();
                var best=candidates.OrderByDescending(x=>x.Take(6).Sum()).FirstOrDefault();
                if(best is not null)for(var stat=0;stat<6;stat++)Apply(side,slot,new(Trigger.None,Operation.Add,Target.Self,(Stat)stat,best[stat]*effect.Amount,effect.Permanent));
                return;
            }
            if(effect.Operation==Operation.TeamHp)
            {
                var amount=effect.Percent?teams[side].MaxHp*effect.Amount:effect.Amount;
                if(effect.ProgressCounter is { } hpKey)amount+=teams[side].Units[slot]?.Unit.Counters.GetValueOrDefault("ability:"+hpKey)??0;
                teams[side].MaxHp+=amount;teams[side].Hp+=amount;return;
            }
            if(effect.Operation==Operation.GrantTrinket)
            {
                for(var copy=0;copy<Math.Max(1,(int)effect.Amount);copy++)
                {
                    var pool=rules.Trinkets.Where(x=>(effect.DefinitionId is null||x.Id==effect.DefinitionId) &&
                        (effect.Rarity is null||x.Rarity==effect.Rarity) && (!effect.NonUnique||!x.Unique) &&
                        (!x.Unique||!teams[side].Bag.Any(b=>b.Id==x.Id))).ToArray();
                    if(pool.Length==0)throw new InvalidOperationException("No eligible combat trinket grant.");
                    var definition=pool[rng.Index(pool.Length)];teams[side].Bag.Add(new(definition.Id,1,teams[side].NextInventoryInstance++));
                    teams[side].Inventory = null;
                    for(var actor=0;actor<6;actor++)Activate(side,actor,Trigger.TrinketGained);
                }
                return;
            }
            if(effect.Operation==Operation.Cleanse)
            { for(var i=0;i<3;i++) teams[side].Status[i]=Math.Max(0,teams[side].Status[i]-(effect.Percent?Math.Ceiling(teams[side].Status[i]*effect.Amount):effect.Amount)); return; }
            foreach(var recipient in slots)
            {
                if (affectsStats) Invalidate();
                var unit=team.Units[recipient]!;
                switch(effect.Operation)
                {
                    case Operation.GrowAbility:
                        var abilityKey="ability:"+effect.ProgressCounter;
                        unit.Unit.Counters[abilityKey]=unit.Unit.Counters.GetValueOrDefault(abilityKey)+effect.Amount;
                        if(effect.Permanent)unit.PersistentUnit.Counters[abilityKey]=unit.Unit.Counters[abilityKey];break;
                    case Operation.Transform:
                        var oldSpecies=_species[unit.Unit.SpeciesId];
                        var id=effect.DefinitionId??throw new InvalidDataException("Transformation target missing.");
                        if(effect.GrantTransformStat)
                        {unit.Unit.Bonuses[(int)effect.Stat]+=effect.Amount;if(effect.Permanent)unit.PersistentUnit.Bonuses[(int)effect.Stat]+=effect.Amount;}
                        var tier=id=="same-rarity"?oldSpecies.Rarity:id=="above-shop"?Math.Min(5,HighestRarity(side)+(int)effect.Amount):-1;
                        var pool=id is "same-rarity" or "above-shop"?_species.Values.Where(x=>x.Rarity==tier &&
                            (x.ShopEligible && x.Pantra || teams[recipientSide].ForeignPool.Contains(x.Id))).ToArray():[_species[id]];
                        if(pool.Length==0)throw new InvalidOperationException("No eligible battle transformation at rarity "+tier);
                        unit.Unit.SpeciesId=pool[rng.Index(pool.Length)].Id;unit.Progress=0;unit.CancelCasts();unit.Timers.Clear();
                        if(effect.ReviveTargets)unit.Alive=true;
                        if(effect.Permanent)unit.PersistentUnit.SpeciesId=unit.Unit.SpeciesId;break;
                    case Operation.CopyStat:
                        Apply(recipientSide,recipient,new(Trigger.None,Operation.Add,Target.Self,effect.Stat,Stats(recipientSide,recipient)[(int)effect.ScaleStat]*effect.Amount,effect.Permanent));break;
                    case Operation.ProgressEvolution:
                        var key=EvolutionRules.Counter(unit.Unit,effect);
                        var progress=unit.Unit.Counters.GetValueOrDefault(key)+(effect.ProgressStat is { } progressStat?eventStats![(int)progressStat]:1);
                        unit.Unit.Counters[key]=progress;if(effect.Permanent)unit.PersistentUnit.Counters[key]=progress;
                        if(progress>=effect.Amount && !effect.DeferredEvolution)Apply(recipientSide,recipient,effect with {Operation=Operation.Evolve,ProgressCounter=null});break;
                    case Operation.Evolve:
                        var evolved=_species[unit.Unit.SpeciesId].EvolutionId??throw new InvalidDataException("Evolution target missing.");
                        unit.Unit.SpeciesId=evolved;unit.Progress=0;unit.CancelCasts();unit.Timers.Clear();
                        if(effect.Permanent)unit.PersistentUnit.SpeciesId=evolved;break;
                    case Operation.Add:
                        if(unit.Unit.SpeciesId=="zephyrex" && effect.Stat==Stat.Multicast)break;
                        (effect.Permanent?unit.Unit.Bonuses:unit.Temporary)[(int)effect.Stat]+=Gain(side,effect.Stat,effect.Amount);
                        if(effect.Permanent)unit.PersistentUnit.Bonuses[(int)effect.Stat]+=Gain(side,effect.Stat,effect.Amount);break;
                    case Operation.Multiply:
                        if(unit.Unit.SpeciesId=="zephyrex" && effect.Stat==Stat.Multicast)break;
                        if (balance28 && !effect.Permanent)
                        {
                            (unit.TemporaryPercentages ??= new double[9])[(int)effect.Stat] += effect.Amount;
                            break;
                        }
                        var bonus=Base(unit)[(int)effect.Stat]*effect.Amount;
                        (effect.Permanent?unit.Unit.Bonuses:unit.Temporary)[(int)effect.Stat]+=bonus;
                        if(effect.Permanent)unit.PersistentUnit.Bonuses[(int)effect.Stat]+=bonus;break;
                    case Operation.Charge: Charge(recipientSide, recipient, unit, effect.Amount); break;
                    case Operation.Trigger: Sequence(recipientSide,recipient); break;
                    case Operation.Protect: unit.Protect+=(int)effect.Amount; break;
                    case Operation.Disable: unit.Disabled=true; break;
                    case Operation.Knockout:
                        if(!unit.Alive)break;
                        if(unit.Protect>0) { unit.Protect--; break; }
                        var before=Stats(recipientSide,recipient);
                        unit.Alive=false;unit.CancelCasts();Activate(recipientSide,recipient,Trigger.Knockout);
                        NotifyKnockout(recipientSide,recipient,before);
                        if(effect.KnockoutStat is { } gained)Apply(side,slot,new(Trigger.None,Operation.Add,Target.Self,gained,effect.KnockoutBonus,effect.KnockoutPermanent));
                        if(effect.StealKnockoutStats)for(var stat=0;stat<8;stat++)if(stat!=6)
                            Apply(side,slot,new(Trigger.None,Operation.Add,Target.Self,(Stat)stat,before[stat]));
                        break;
                    case Operation.Revive:
                        if(!unit.Alive) {unit.Alive=true;unit.Temporary[(int)effect.Stat]+=effect.Amount;}break;
                    case Operation.LevelUp:
                        var oldLevel=unit.Unit.Level;
                        unit.Unit.Level=effect.UnlimitedLevels?checked(unit.Unit.Level+1):Math.Min(effect.MaximumLevel??4,unit.Unit.Level+1);
                        var currentSpecies=_species[unit.Unit.SpeciesId];
                        if(currentSpecies.EvolutionId is { } levelEvolution && currentSpecies.EvolutionLevel is { } threshold && unit.Unit.Level>=threshold)
                        {
                            unit.Unit.SpeciesId=levelEvolution;
                            if (balance28) { unit.Progress = 0; unit.CancelCasts(); }
                            if(effect.Permanent)unit.PersistentUnit.SpeciesId=levelEvolution;
                        }
                        if(effect.Permanent)unit.PersistentUnit.Level=unit.Unit.Level;
                        if(unit.Unit.Level!=oldLevel){unit.Timers.Clear();LevelChanged(recipientSide,recipient);NotifyAllies(recipientSide,recipient,Trigger.AllyAnyLevelUp);}break;
                    case Operation.AddedCooldown: unit.AddedCooldown+=effect.Amount; break;
                    case Operation.ActivateAbility:
                        for(var repeat=0;repeat<effect.Amount;repeat++)Activate(recipientSide,recipient,effect.SubjectTrigger);break;
                    case Operation.AddType:
                        if(effect.Type is not null)
                        {unit.Unit.AddedTypes=unit.Unit.AddedTypes.Append(effect.Type).Distinct().ToArray();
                            if(effect.Permanent)unit.PersistentUnit.AddedTypes=unit.PersistentUnit.AddedTypes.Append(effect.Type).Distinct().ToArray();}break;
                    default: throw new NotSupportedException($"Combat handler missing: {effect.Operation}");
                }
            }
            }finally{if (affectsStats) Invalidate();}
        }
        int BagCount(int side,string id) => InventoryState(side).Counts.GetValueOrDefault(id);
        IEnumerable<string> Types(Fighter unit)=>UnitTypes.Resolve(unit.Unit,_species);
        double Gain(int side,Stat stat,double value)=>stat is Stat.Multicast or Stat.Speed?value:value*(1+.8*BagCount(side,"zenith-stone"));
        void Consume(int side, string id)
        {
            var team = teams[side];
            var index = team.Bag.FindIndex(entry => entry.Id == id);
            if (index < 0) return;
            var entry = team.Bag[index];
            if (entry.Quantity <= 1) team.Bag.RemoveAt(index);
            else team.Bag[index] = entry with { Quantity = entry.Quantity - 1 };
            team.Inventory = null;
            Invalidate();
        }
        void LevelChanged(int side,int slot){
            var unit=teams[side].Units[slot]!;
            if(unit.Unit.Level==3 && BagCount(side,"mega-upgrade-disc")>0){Consume(side,"mega-upgrade-disc");Apply(side,slot,new(Trigger.None,Operation.LevelUp,Target.Self,Permanent:true));}
            teams[side].Counters["chemist-levels"]=teams[side].Counters.GetValueOrDefault("chemist-levels")+teams[side].Trainers.Count(x=>x=="chemist");
            Invalidate();
        }
        void Special(int side,int slot,string id)
        {
            var team=teams[side];var alive=Enumerable.Range(0,6).Where(i=>team.Units[i] is {Alive:true}).ToArray();
            switch(id){
                case "alphas-crown":
                    if(team.Units[1] is not {Alive:true})return;
                    var top=Stats(side,1)[0];var damage=alive.Select(i=>Stats(side,i)[0]).ToArray();
                    for(var i=0;i<alive.Length;i++)team.Units[alive[i]]!.DamageAdjustment+=top-damage[i];return;
                case "ancient-plume":
                    if(alive.Length!=1 || BagCount(side,id)==0)return;
                    Apply(side,alive[0],new(Trigger.None,Operation.Add,Target.Self,Stat.Multicast,1,true));Consume(side,id);return;
                case "candy-jar":
                    foreach(var i in alive)if(_species[team.Units[i]!.Unit.SpeciesId].Rarity<=1 && team.Units[i]!.Unit.Level<3){while(team.Units[i]!.Unit.Level<3)Apply(side,i,new(Trigger.None,Operation.LevelUp,Target.Self,Permanent:true));}return;
                case "metal-bat":
                    var best=alive.OrderByDescending(i=>BasisCooldown(team.Units[i]!)??0).FirstOrDefault(-1);
                    if(best>=0)Apply(side,best,new(Trigger.None,Operation.Multiply,Target.Self,Amount:.5));return;
                case "fake-diamond":if(BagCount(side,id)>0){team.Gold+=40;Consume(side,id);}return;
                case "locked-box":if(BagCount(side,id)>0){Consume(side,id);Apply(side,slot,new(Trigger.None,Operation.GrantTrinket,Target.All,Rarity:HighestRarity(side),NonUnique:true));}return;
                case "black-belt":
                    var fighters=alive.Where(i=>Types(team.Units[i]!).Contains("fighting")).ToArray();if(fighters.Length==1)Activate(side,fighters[0],Trigger.Victory);return;
                default:throw new NotSupportedException("Combat special rule "+id);
            }
        }
        int HighestRarity(int side)
        {
            var highest=ShopRankOdds.ForRank(teams[side].Rank).Where(x=>x.Probability>0).Max(x=>(int)x.Rarity);
            var inventory=teams[side].Bag.SelectMany(entry=>_trinkets[entry.Id].Effects).Concat(teams[side].Trainers.SelectMany(x=>_trainers[x].Effects))
                .Where(x=>x.MinimumDay<=teams[side].Day && x.Operation==Operation.ShopModifier).ToArray();
            if(inventory.Any(x=>x.ShopTrait==ShopTrait.LegendaryWeight && x.Amount>0))highest=4;
            var minimum=inventory.Where(x=>x.ShopTrait==ShopTrait.MinimumRarity).Select(x=>(int)x.Amount).DefaultIfEmpty(0).Max();
            return Math.Max(highest,minimum);
        }
    }
    private sealed class Fighter(Unit unit)
    {
        public Unit Unit=unit.Clone(),PersistentUnit=unit.Clone(); public bool Alive=true,Disabled;public long Epoch;
        public int Casts; public double Damage, Healing, ShieldGranted;
        public double Progress;public double DamageAdjustment; public double AddedCooldown=0; public int Protect; public double[] Temporary=new double[9];
        public double[]? TemporaryPercentages;
        public long NextCastTick;
        public long PendingCasts;
        public void CancelCasts()
        {
            Epoch++;
            PendingCasts = 0;
        }
        public HashSet<Effect> ConsumedEffects=[];
        public Dictionary<Effect,double> Growth=[];
        public Dictionary<Effect,double> Timers=[];
        public Dictionary<string,double> Activations=[];
        public int? ParentSlot;public bool TemporarySpawn;
    }
    private sealed class InventorySummary(List<BagEntry> entries, Dictionary<string, InventoryDefinition> definitions)
    {
        private readonly ILookup<Trigger, (Effect Effect, int Count)> _effects = entries
            .SelectMany(entry => definitions[entry.Id].Effects.Select(effect => (Effect: effect, Count: entry.Quantity)))
            .ToLookup(entry => entry.Effect.Trigger);
        public Dictionary<string, int> Counts { get; } = entries.GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Quantity));
        public bool Linked { get; } = entries.Any(entry => definitions[entry.Id].Effects.Any(effect => effect.Operation == Operation.LinkAllies));
        public IEnumerable<(Effect Effect, int Count)> Effects(Trigger trigger) => _effects[trigger];
    }

    private sealed class Side(FrozenBoard board)
    {
        public Fighter?[] Units=board.Team.Select(x=>x is null?null:new Fighter(x)).ToArray();
        public bool Mirrored = board.Mirrored;
        public InventorySummary? Inventory;
        public double MaxHp,Hp,Shield,Gold; public double[] Status=new double[3]; public List<BagEntry> Bag=[..board.Bag];
        public Dictionary<string,double> Counters=board.Counters is null?[]:new(board.Counters);
        public List<PurchaseBuff> PendingPurchaseBuffs=[];
        public string[] Trainers=board.ExtraTrainers.Prepend(board.Trainer).ToArray();
        public int Rank=board.Rank,Day=board.Day,Wins=board.Wins;public string[] ForeignPool=board.ForeignPool is null?[]:[..board.ForeignPool];
        public long NextInventoryInstance=board.Bag.Select(x=>x.Instance).DefaultIfEmpty(0).Max()+1;
        public long NextUnitInstance=board.Team.OfType<Unit>().Select(x=>x.Instance).DefaultIfEmpty(0).Max()+1;
    }
}
