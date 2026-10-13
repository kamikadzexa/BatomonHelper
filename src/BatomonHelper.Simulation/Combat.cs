using BatomonHelper.Core;
namespace BatomonHelper.Simulation;

public sealed class Combat(RulesManifest rules)
{
    private readonly Dictionary<string, Species> _species = rules.Species.ToDictionary(x => x.Id);
    private readonly Dictionary<string, InventoryDefinition> _trinkets = rules.Trinkets.ToDictionary(x => x.Id);
    private readonly Dictionary<string, InventoryDefinition> _trainers = rules.Trainers.ToDictionary(x => x.Id);
    private readonly bool _hasAmplifiers=rules.Species.Any(s=>s.Normal.Concat(s.Shiny).Any(l=>l.Effects.Any(e=>e.Operation==Operation.RepeatOngoing)));
    private readonly bool _hasCooldownAuras=rules.Species.Any(s=>s.Normal.Concat(s.Shiny).Any(l=>l.Effects.Any(e=>e.Trigger==Trigger.Ongoing && e.Operation==Operation.AddedCooldown)));
    public static double EffectiveCooldown(double basis, double speed, double added = 0) => Math.Max(.1, basis / Math.Max(.1, 1 + speed) + added);
    public static double Absorb(double damage, ref double shield, bool status)
    {
        if (status && shield > 0) damage *= .85;
        var absorbed = Math.Min(shield, damage); shield -= absorbed; return damage - absorbed;
    }
    public static int[] Targets(int slot, Target target, bool linked = false) => target switch
    {
        Target.Self => [slot], Target.Above => slot >= 3 ? [slot-3] : [], Target.Below => slot < 3 ? [slot+3] : [],
        Target.Left => slot % 3 > 0 ? [slot-1] : [], Target.Right => slot % 3 < 2 ? [slot+1] : [],
        Target.Adjacent => Enumerable.Range(0,6).Where(i => i != slot && (linked || i/3 == slot/3 && Math.Abs(i-slot)==1 || Math.Abs(i-slot)==3)).ToArray(),
        Target.Row or Target.EnemyRow or Target.BothRows => Enumerable.Range(slot/3*3,3).ToArray(), Target.Column => [slot%3,slot%3+3], Target.Opposite => [slot], Target.BottomRight => [5],
        Target.TopMiddle => [1], Target.LeftColumn => [0,3], Target.RightColumn => [2,5],
        _ => [0,1,2,3,4,5]
    };
    public BattleResult ResolveBattle(FrozenBoard player, FrozenBoard opponent, ulong seed, bool trace = false, bool reference = false)
    {
        rules.RequireReady();
        if (player.Day != opponent.Day) throw new ArgumentException("Opponent must be from the same day.");
        var rng = new SeedStream(seed);
        var teams = new[] { new Side(player), new Side(opponent) };
        var queue = new PriorityQueue<(int Side,int Slot,long Epoch), (long Tick,long Order)>();
        var log = new List<string>(); long order = 0; long now = 0; long operations = 0;
        var tickSeconds = rules.Profile?.CombatTickSeconds??.01;
        long Tick(double seconds) => (long)Math.Round(seconds/tickSeconds,MidpointRounding.AwayFromZero);
        for (var side=0; side<2; side++)
        {
            teams[side].MaxHp = side == 0 ? player.MaxHp : opponent.MaxHp;
            teams[side].Hp = teams[side].MaxHp;
            double flatHp=0,percentHp=0;
            void HpBonus(Effect effect,Fighter? source=null){
                if(effect.Percent)percentHp+=effect.Amount;else flatHp+=effect.Amount;
                if(effect.ProgressCounter is { } key)flatHp+=source?.Unit.Counters.GetValueOrDefault("ability:"+key)??0;
            }
            foreach(var entry in teams[side].Bag)foreach(var effect in _trinkets[entry.Id].Effects.Where(x=>x.Operation==Operation.TeamHp && x.Trigger==Trigger.Ongoing))
                for(var copy=0;copy<entry.Quantity;copy++)HpBonus(effect);
            for(var slot=0;slot<6;slot++)if(teams[side].Units[slot] is {Alive:true,Disabled:false} u){
                if(teams[side].Trainers.Contains("pyromaniac") && Types(u).Count()==1){u.Unit.AddedTypes=u.Unit.AddedTypes.Append("fire").Distinct().ToArray();u.PersistentUnit.AddedTypes=[..u.Unit.AddedTypes];}
                foreach(var effect in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.TeamHp))
                    for(var repeat=0;repeat<=OngoingRepeats(side,slot);repeat++)HpBonus(effect,u);
            }
            teams[side].MaxHp=(teams[side].MaxHp+flatHp)*(1+percentHp);teams[side].Hp=teams[side].MaxHp;
        }
        for(var side=0;side<2;side++)
        {
            Inventory(side,1,Trigger.BattleStart);
            var startRepeats=(int)teams[side].Counters.GetValueOrDefault("next-battle-start-repeat")+
                teams[side].Bag.Sum(entry=>entry.Quantity*_trinkets[entry.Id].Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.NextBattleStartRepeat).Sum(x=>(int)x.Amount));
            for (var slot=0; slot<6; slot++)
                if (teams[side].Units[slot] is { } unit)
                    for(var repeat=0;repeat<=startRepeats;repeat++)Activate(side,slot,Trigger.BattleStart);
        }
        long nextBurn=Tick(.5),nextPoison=Tick(1),nextStorm=Tick(30.5);
        while (true)
        {
            if (++operations > 1_000_000) throw new InvalidOperationException("Battle event limit exceeded; preserve the seed for a rules audit.");
            var next = now+1;
            if (!reference)
            {
                next = Math.Min(nextBurn, Math.Min(nextPoison,nextStorm));
                if (queue.TryPeek(out _,out var priority)) next=Math.Min(next,priority.Tick);
                for(var side=0;side<2;side++) for(var slot=0;slot<6;slot++)
                    if(teams[side].Units[slot] is { Alive:true, ParentSlot:null } u && Definition(u).Cooldown is { } basis)
                    {
                        var remaining=EffectiveCooldown(basis,Stats(side,slot)[(int)Stat.Speed],u.AddedCooldown+OngoingCooldown(side,slot))-u.Progress;
                        next=Math.Min(next,now+Math.Max(1,(long)Math.Ceiling((remaining-1e-9)/tickSeconds)));
                    }
                for(var side=0;side<2;side++)for(var slot=0;slot<6;slot++)if(teams[side].Units[slot] is {Alive:true,Disabled:false} u)
                    foreach(var effect in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Timer))
                        next=Math.Min(next,now+Math.Max(1,(long)Math.Ceiling((effect.IntervalSeconds-u.Timers.GetValueOrDefault(effect)-1e-9)/tickSeconds)));
                next=Math.Max(now+1,next);
            }
            var elapsed=(next-now)*tickSeconds;
            foreach(var team in teams) foreach(var u in team.Units) if(u is { Alive:true })
            {
                u.Progress+=elapsed;
                if(!u.Disabled)foreach(var effect in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Timer))u.Timers[effect]=u.Timers.GetValueOrDefault(effect)+elapsed;
            }
            now=next;
            for(var side=0;side<2;side++)for(var slot=0;slot<6;slot++)if(teams[side].Units[slot] is {Alive:true,Disabled:false} u)
                foreach(var effect in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Timer))if(u.Timers.GetValueOrDefault(effect)+1e-9>=effect.IntervalSeconds)
                {u.Timers[effect]=0;for(var repeat=0;repeat<=OngoingRepeats(side,slot);repeat++)Apply(side,slot,effect);}
            for(var side=0;side<2;side++) for(var slot=0;slot<6;slot++)
                if(teams[side].Units[slot] is { Alive:true, ParentSlot:null } u && Definition(u).Cooldown is { } basis &&
                    u.Progress+1e-9>=EffectiveCooldown(basis,Stats(side,slot)[(int)Stat.Speed],u.AddedCooldown+OngoingCooldown(side,slot)))
                { u.Progress=0; Sequence(side,slot); }
            var casts=new List<(int Side,int Slot,double[] Stats)>();
            while(queue.TryPeek(out var casting,out var at) && at.Tick<=now)
            {
                queue.Dequeue();
                if(teams[casting.Side].Units[casting.Slot] is not { Alive:true } caster || caster.Epoch!=casting.Epoch) continue;
                Activate(casting.Side,casting.Slot,Trigger.Cast);
                for(var child=0;child<6;child++)if(teams[casting.Side].Units[child] is {Alive:true} summoned && summoned.ParentSlot==casting.Slot)
                    Sequence(casting.Side,child,casting.Slot);
                Inventory(casting.Side,casting.Slot,Trigger.Cast);
                casts.Add((casting.Side,casting.Slot,Stats(casting.Side,casting.Slot)));
                NotifyAllies(casting.Side,casting.Slot,Trigger.AllyCast);
                if(trace) log.Add($"{now*tickSeconds:F2}s side {casting.Side} slot {casting.Slot}: {caster.Unit.SpeciesId} cast");
            }
            // All casts on this tick share the published Shield -> Damage -> Status -> Heal phases.
            foreach(var cast in casts)
            {teams[cast.Side].Shield+=Math.Max(0,cast.Stats[(int)Stat.Shield])*(1+.25*BagCount(cast.Side,"topaz-amulet"));if(cast.Stats[(int)Stat.Shield]>0)
                {NotifyAllies(cast.Side,cast.Slot,Trigger.AllyShield,cast.Stats);NotifyAllies(cast.Side,cast.Slot,Trigger.TeamShield,cast.Stats,true);}}
            foreach(var cast in casts)
            {
                var enemy=teams[1-cast.Side]; var direct=Math.Max(0,cast.Stats[(int)Stat.Damage]);
                if(direct>0) { enemy.Hp-=Absorb(enemy.Status[2],ref enemy.Shield,true); enemy.Hp-=Absorb(direct,ref enemy.Shield,false);
                    NotifyAllies(cast.Side,cast.Slot,Trigger.TeamDamage,cast.Stats,true);}
            }
            foreach(var cast in casts) for(var status=0;status<3;status++)
            {
                var amount=Math.Max(0,cast.Stats[(int)Stat.Burn+status]);teams[1-cast.Side].Status[status]+=amount;
                if(amount>0) {NotifyAllies(cast.Side,cast.Slot,new[]{Trigger.AllyBurn,Trigger.AllyPoison,Trigger.AllyShock}[status],cast.Stats);
                    if(status==0)NotifyAllies(cast.Side,cast.Slot,Trigger.TeamBurn,cast.Stats,true);}
            }
            foreach(var cast in casts) teams[cast.Side].Hp=Math.Min(teams[cast.Side].MaxHp,teams[cast.Side].Hp+Math.Max(0,cast.Stats[(int)Stat.Heal])*(1+.25*BagCount(cast.Side,"sapphire-amulet")));
            if(now>=nextBurn)
            {
                foreach(var team in teams) { team.Hp-=Absorb(team.Status[0],ref team.Shield,true); team.Status[0]=Math.Max(0,team.Status[0]-1); }
                nextBurn+=Tick(.5);
            }
            if(now>=nextPoison) { foreach(var team in teams) team.Hp-=Absorb(team.Status[1],ref team.Shield,true); nextPoison+=Tick(1); }
            if(now>=nextStorm)
            {
                var damage=8*Math.Pow(2,(now*tickSeconds-30)/3);
                foreach(var team in teams) team.Hp-=Absorb(damage,ref team.Shield,false);
                nextStorm+=Tick(.5);
            }
            if(teams.Any(x=>x.Hp<=0)) break;
        }
        var won=teams[1].Hp<=0;
        for(var slot=0;slot<6;slot++) if(teams[0].Units[slot] is not null) Activate(0,slot,won?Trigger.Victory:Trigger.Defeat);
        Inventory(0,1,won?Trigger.Victory:Trigger.Defeat);
        return new(won,teams.All(x=>x.Hp<=0),now*tickSeconds,teams[0].Hp,teams[1].Hp,
            teams[0].Units.Select(x=>x is {TemporarySpawn:false}?x.PersistentUnit.Clone():null).ToArray(),[..teams[0].Bag],teams[0].Gold,[..log],new(teams[0].Counters),[..teams[0].PendingPurchaseBuffs]);

        LevelDefinition Definition(Fighter u)
        {
            return LevelRules.Resolve(_species[u.Unit.SpeciesId],u.Unit.Level,u.Unit.Shiny);
        }
        bool Matches(Fighter u,Effect effect) => (effect.Operation==Operation.AddType || effect.Type is null || effect.Type.Split('|').Any(_species[u.Unit.SpeciesId].Types.Concat(u.Unit.AddedTypes).Contains)) &&
            u.Unit.SpeciesId!=effect.ExcludeSpecies && (effect.Rarity is null || _species[u.Unit.SpeciesId].Rarity==effect.Rarity) &&
            (effect.MinimumLevel is null || u.Unit.Level>=effect.MinimumLevel) && (effect.MaximumLevel is null || u.Unit.Level<=effect.MaximumLevel) &&
            (!effect.RequireNoAbility||Definition(u).Effects.Length==0) && (!effect.RequireOngoing||Definition(u).AbilityTrigger==Trigger.Ongoing || Definition(u).Effects.Any(x=>x.Trigger==Trigger.Ongoing));
        double[] Base(Fighter u) => Definition(u).Stats.Select((x,i)=>i==(int)Stat.Speed?x+u.Unit.Bonuses[i]+u.Temporary[i]:Math.Max(0,x+u.Unit.Bonuses[i]+u.Temporary[i])).ToArray();
        UInt128 StatBit(int side,int slot,Stat stat) => (UInt128)1<<((side*6+slot)*9+(int)stat);
        double Scale(int side,int slot,Effect effect,UInt128 excludedDynamicStats=default,double[]? eventStats=null) => effect.Scale==EffectScale.None?1:EffectScaling.Factor(effect,teams[side].Units[slot]?.Unit,
            teams[side].Units.Select((x,i)=>(x,i)).Where(x=>x.x is {Alive:true} && (effect.Scale!=EffectScale.AlliedStat ||
                x.i!=slot && x.x.Unit.SpeciesId!=effect.ScaleExcludeSpecies && Targets(slot,effect.ScaleTarget,Linked(side)).Contains(x.i))).Select(x=>x.x!.Unit),teams[side].Bag,_species,
            unit=>ScaleStats(side,unit,effect.Trigger==Trigger.Ongoing?excludedDynamicStats | StatBit(side,slot,effect.Stat):excludedDynamicStats),
            teams[1-side].Status,teams[side].Counters.GetValueOrDefault("lives-lost"),_trinkets,eventStats,teams[side].Wins,
            teams[side].Counters.GetValueOrDefault("items-used-rarity-"+effect.ScaleRarity));
        double[] ScaleStats(int side,Unit unit,UInt128 excludedDynamicStats)
        {
            var values=Stats(side,Array.FindIndex(teams[side].Units,x=>ReferenceEquals(x?.Unit,unit)),excludedDynamicStats);
            values[(int)Stat.SellValue]+=Math.Ceiling((_species[unit.SpeciesId].Cost*(unit.Level switch {1=>1,2=>3,_=>6})+unit.StoredDiscount)/2);
            return values;
        }
        double[] Stats(int side,int slot,UInt128 excludedDynamicStats=default)
        {
            var source=teams[side].Units[slot]!; var values=Base(source);var percentages=new double[9];
            void Bonus(Effect e,int actor)
            {
                if(e.Scale!=EffectScale.None && (excludedDynamicStats & StatBit(side,actor,e.Stat))!=0)return;
                var amount=e.Amount*Scale(side,actor,e,excludedDynamicStats);
                if(e.Operation==Operation.Add)values[(int)e.Stat]+=amount;
                if(e.Operation==Operation.Multiply)percentages[(int)e.Stat]+=amount;
            }
            for(var actor=0;actor<6;actor++)
                if(teams[side].Units[actor] is { Alive:true, Disabled:false } u)
                    foreach(var e in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Ongoing))
                        if((e.IncludeSelf && actor==slot || Targets(actor,e.Target,Linked(side)).Contains(slot)) &&
                            (e.IncludeSelf && actor==slot || Matches(source,e)))
                        for(var repeat=0;repeat<=OngoingRepeats(side,actor);repeat++)Bonus(e,actor);
            foreach(var entry in teams[side].Bag)
                foreach(var e in _trinkets[entry.Id].Effects.Where(x=>x.Trigger==Trigger.Ongoing))
                    if(Matches(source,e) && Targets(1,e.Target).Contains(slot))
                    for(var copy=0;copy<entry.Quantity;copy++)
                    Bonus(e,slot);
            foreach(var trainer in teams[side].Trainers)
                foreach(var e in _trainers[trainer].Effects.Where(x=>x.Trigger==Trigger.Ongoing))
                    if(Matches(source,e))Bonus(e,slot);
            var cooldown=Definition(source).Cooldown??0;
            if(cooldown>=5){percentages[0]+=.2*BagCount(side,"small-club");for(var i=0;i<6;i++)percentages[i]+=.7*BagCount(side,"giant-club");}
            var types=Types(source).ToHashSet();
            if(!teams[side].Units.Where((u,i)=>i!=slot && u is {Alive:true}).OfType<Fighter>().Any(u=>Types(u).Any(types.Contains)))values[6]+=.25*BagCount(side,"kaleidoscope");
            foreach(var trainer in teams[side].Trainers){
                if(trainer=="musician" && types.Contains("electric") && teams[side].Units.OfType<Fighter>().Count(u=>u.Alive && Types(u).Contains("electric"))==1)percentages[5]+=1.5;
                if(trainer=="chemist" && types.Contains("toxic"))values[4]+=3+teams[side].Counters.GetValueOrDefault("chemist-levels");
                if(trainer=="pyromaniac" && types.Contains("fire"))values[3]+=2;
            }
            for(var i=0;i<9;i++)values[i]*=1+percentages[i];
            for(var i=0;i<6;i++) values[i]=Math.Clamp(values[i],0,999_999_999);
            values[0]+=source.DamageAdjustment;
            values[(int)Stat.Multicast]=Math.Max(1,values[(int)Stat.Multicast]);
            if(source.Unit.SpeciesId=="zephyrex")values[(int)Stat.Multicast]=1;
            return values;
        }
        void Sequence(int side,int slot,int? parent=null)
        {
            if(teams[side].Units[slot]!.ParentSlot is { } required && required!=parent)return;
            var repeats=(int)Stats(side,slot)[(int)Stat.Multicast];
            for(var repeat=0;repeat<repeats;repeat++) queue.Enqueue((side,slot,teams[side].Units[slot]!.Epoch),(now+repeat*Tick(.1),order++));
        }
        bool Linked(int side) => teams[side].Bag.Any(x=>_trinkets[x.Id].Effects.Any(e=>e.Operation==Operation.LinkAllies));
        int OngoingRepeats(int side,int slot)
        {
            if(!_hasAmplifiers)return 0;
            var extra=0;
            for(var actor=0;actor<6;actor++)if(teams[side].Units[actor] is {Alive:true,Disabled:false} u)
                foreach(var e in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.RepeatOngoing))
                    if(Targets(actor,e.Target,Linked(side)).Contains(slot))extra+=(int)e.Amount;
            return extra;
        }
        double OngoingCooldown(int side,int slot)
        {
            if(!_hasCooldownAuras)return 0;
            var delta=0d;
            for(var owner=0;owner<2;owner++)for(var actor=0;actor<6;actor++)if(teams[owner].Units[actor] is {Alive:true,Disabled:false} u)
                foreach(var e in Definition(u).Effects.Where(x=>x.Trigger==Trigger.Ongoing && x.Operation==Operation.AddedCooldown))
                    if((e.Target==Target.BothRows || owner==side && e.Target!=Target.EnemyRow || owner!=side && e.Target==Target.EnemyRow) &&
                        Targets(actor,e.Target,Linked(owner)).Contains(slot))delta+=e.Amount*(1+OngoingRepeats(owner,actor));
            return delta;
        }
        void NotifyAllies(int side,int actor,Trigger trigger,double[]? eventStats=null,bool includeSelf=false)
        {
            for(var slot=0;slot<6;slot++)if((includeSelf || slot!=actor) && teams[side].Units[slot] is { Alive:true,Disabled:false } u)
                React(side,slot,Definition(u).Effects.Where(x=>x.Trigger==trigger && EventMatches(side,slot,side,actor,x) &&
                    (x.EventType is null||_species[teams[side].Units[actor]!.Unit.SpeciesId].Types.Concat(teams[side].Units[actor]!.Unit.AddedTypes).Contains(x.EventType))).ToArray(),eventStats,actor);
        }
        bool EventMatches(int watcherSide,int watcherSlot,int actorSide,int actorSlot,Effect e)
        {
            var actor=teams[actorSide].Units[actorSlot]!.Unit;
            return actor.SpeciesId!=e.EventExcludeSpecies && (e.EventMinimumLevel is null || actor.Level>=e.EventMinimumLevel) &&
                (!e.EventAdjacent || actorSide==watcherSide && Targets(watcherSlot,Target.Adjacent,Linked(watcherSide)).Contains(actorSlot));
        }
        void NotifyKnockout(int actorSide,int actorSlot,double[] before)
        {
            for(var side=0;side<2;side++)for(var slot=0;slot<6;slot++)
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
            foreach(var effect in definition.Effects.Where(x=>x.Trigger==trigger))
                for(var repeat=0;repeat<=repeats;repeat++)
                {if(origin!=source.Unit.SpeciesId)return;Apply(side,slot,effect);}
        }
        void Inventory(int side,int slot,Trigger trigger)
        {
            foreach(var entry in teams[side].Bag.ToArray())
                foreach(var effect in _trinkets[entry.Id].Effects.Where(x=>x.Trigger==trigger))
                    for(var count=0;count<entry.Quantity;count++) Apply(side,slot,effect);
            foreach(var trainer in teams[side].Trainers)
                foreach(var effect in _trainers[trainer].Effects.Where(x=>x.Trigger==trigger))Apply(side,slot,effect);
        }
        void Apply(int side,int slot,Effect effect,double[]? eventStats=null,int eventSlot=-1)
        {
            if(++operations>1_000_000) throw new InvalidOperationException("Recursive trigger limit exceeded.");
            if(effect.Operation is Operation.InventoryRule or Operation.TrainerRule){Special(side,slot,effect.DefinitionId!);return;}
            if(effect.Target==Target.BothRows)
            {Apply(side,slot,effect with {Target=Target.Row},eventStats);Apply(side,slot,effect with {Target=Target.EnemyRow},eventStats);return;}
            if(effect.Target==Target.BothTeams)
            {Apply(side,slot,effect with {Target=Target.All},eventStats);Apply(side,slot,effect with {Target=Target.Enemies},eventStats);return;}
            if(effect.OncePerBattle && (teams[side].Units[slot] is not { } owner || !owner.ConsumedEffects.Add(effect)))return;
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
            var enemy=effect.Target is Target.Opposite or Target.Enemies or Target.RandomEnemy or Target.EnemyRow;
            var recipientSide=enemy?1-side:side; var team=teams[recipientSide];
            var slots=(effect.Target==Target.Actor?(eventSlot>=0?[eventSlot]:throw new InvalidOperationException("Actor target requires an event.")):Targets(slot,effect.Target,Linked(side)))
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
                    for(var actor=0;actor<6;actor++)Activate(side,actor,Trigger.TrinketGained);
                }
                return;
            }
            if(effect.Operation==Operation.Cleanse)
            { for(var i=0;i<3;i++) teams[side].Status[i]=Math.Max(0,teams[side].Status[i]-(effect.Percent?Math.Ceiling(teams[side].Status[i]*effect.Amount):effect.Amount)); return; }
            foreach(var recipient in slots)
            {
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
                        unit.Unit.SpeciesId=pool[rng.Index(pool.Length)].Id;unit.Progress=0;unit.Epoch++;unit.Timers.Clear();
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
                        unit.Unit.SpeciesId=evolved;unit.Progress=0;unit.Epoch++;unit.Timers.Clear();
                        if(effect.Permanent)unit.PersistentUnit.SpeciesId=evolved;break;
                    case Operation.Add:
                        if(unit.Unit.SpeciesId=="zephyrex" && effect.Stat==Stat.Multicast)break;
                        (effect.Permanent?unit.Unit.Bonuses:unit.Temporary)[(int)effect.Stat]+=Gain(side,effect.Stat,effect.Amount);
                        if(effect.Permanent)unit.PersistentUnit.Bonuses[(int)effect.Stat]+=Gain(side,effect.Stat,effect.Amount);break;
                    case Operation.Multiply:
                        if(unit.Unit.SpeciesId=="zephyrex" && effect.Stat==Stat.Multicast)break;
                        var bonus=Base(unit)[(int)effect.Stat]*effect.Amount;
                        (effect.Permanent?unit.Unit.Bonuses:unit.Temporary)[(int)effect.Stat]+=bonus;
                        if(effect.Permanent)unit.PersistentUnit.Bonuses[(int)effect.Stat]+=bonus;break;
                    case Operation.Charge: if(unit.Unit.SpeciesId is not("dracana" or "ironcore" or "steamscuttle")) unit.Progress+=effect.Amount; break;
                    case Operation.Trigger: Sequence(recipientSide,recipient); break;
                    case Operation.Protect: unit.Protect+=(int)effect.Amount; break;
                    case Operation.Disable: unit.Disabled=true; break;
                    case Operation.Knockout:
                        if(!unit.Alive)break;
                        if(unit.Protect>0) { unit.Protect--; break; }
                        var before=Stats(recipientSide,recipient);
                        unit.Alive=false;unit.Epoch++;Activate(recipientSide,recipient,Trigger.Knockout);
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
                        {unit.Unit.SpeciesId=levelEvolution;if(effect.Permanent)unit.PersistentUnit.SpeciesId=levelEvolution;}
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
        }
        int BagCount(int side,string id)=>teams[side].Bag.Where(x=>x.Id==id).Sum(x=>x.Quantity);
        IEnumerable<string> Types(Fighter unit)=>_species[unit.Unit.SpeciesId].Types.Concat(unit.Unit.AddedTypes).Distinct();
        double Gain(int side,Stat stat,double value)=>stat is Stat.Multicast or Stat.Speed?value:value*(1+.8*BagCount(side,"zenith-stone"));
        void Consume(int side,string id){var index=teams[side].Bag.FindIndex(x=>x.Id==id);if(index<0)return;var entry=teams[side].Bag[index];if(entry.Quantity<=1)teams[side].Bag.RemoveAt(index);else teams[side].Bag[index]=entry with {Quantity=entry.Quantity-1};}
        void LevelChanged(int side,int slot){
            var unit=teams[side].Units[slot]!;
            if(unit.Unit.Level==3 && BagCount(side,"mega-upgrade-disc")>0){Consume(side,"mega-upgrade-disc");Apply(side,slot,new(Trigger.None,Operation.LevelUp,Target.Self,Permanent:true));}
            teams[side].Counters["chemist-levels"]=teams[side].Counters.GetValueOrDefault("chemist-levels")+teams[side].Trainers.Count(x=>x=="chemist");
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
                    var best=alive.OrderByDescending(i=>Definition(team.Units[i]!).Cooldown??0).FirstOrDefault(-1);
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
        public double Progress;public double DamageAdjustment; public double AddedCooldown=0; public int Protect; public double[] Temporary=new double[9];
        public HashSet<Effect> ConsumedEffects=[];
        public Dictionary<Effect,double> Growth=[];
        public Dictionary<Effect,double> Timers=[];
        public int? ParentSlot;public bool TemporarySpawn;
    }
    private sealed class Side(FrozenBoard board)
    {
        public Fighter?[] Units=board.Team.Select(x=>x is null?null:new Fighter(x)).ToArray();
        public double MaxHp,Hp,Shield,Gold; public double[] Status=new double[3]; public List<BagEntry> Bag=[..board.Bag];
        public Dictionary<string,double> Counters=board.Counters is null?[]:new(board.Counters);
        public List<PurchaseBuff> PendingPurchaseBuffs=[];
        public string[] Trainers=board.ExtraTrainers.Prepend(board.Trainer).ToArray();
        public int Rank=board.Rank,Day=board.Day,Wins=board.Wins;public string[] ForeignPool=board.ForeignPool is null?[]:[..board.ForeignPool];
        public long NextInventoryInstance=board.Bag.Select(x=>x.Instance).DefaultIfEmpty(0).Max()+1;
        public long NextUnitInstance=board.Team.OfType<Unit>().Select(x=>x.Instance).DefaultIfEmpty(0).Max()+1;
    }
}
