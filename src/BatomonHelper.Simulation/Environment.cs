using BatomonHelper.Core;

namespace BatomonHelper.Simulation;

public sealed partial class GameEnvironment(RulesManifest rules)
{
    private readonly Dictionary<string,Species> _species=rules.Species.ToDictionary(x=>x.Id);
    private readonly Dictionary<string,InventoryDefinition> _trinkets=rules.Trinkets.ToDictionary(x=>x.Id);
    private readonly Dictionary<string,InventoryDefinition> _trainers=rules.Trainers.ToDictionary(x=>x.Id);
    private readonly Combat _combat=new(rules);
    private readonly bool _hasOwnedShopTraits=rules.Species.Any(s=>s.Normal.Concat(s.Shiny).Any(l=>l.Effects.Any(e=>e.Operation==Operation.ShopModifier)));
    private SeedStream _random=new(1);
    public RunState State { get; private set; } = new();
    public void Restore(RunState state)
    {
        rules.RequireReady(); _revision++; State=state.Clone(); _random=new(State.RandomState);
    }
    public Observation Reset(ulong seed,string trainer)
    {
        rules.RequireReady();
        if(!rules.Trainers.Any(x=>x.Id==trainer)) throw new ArgumentException("Unknown trainer.");
        _revision++; _random=new(seed);
        State=new() { RunId=$"{seed:X16}-{trainer}",Trainer=trainer,Lives=rules.Parameters.InitialLives,
            Gold=rules.Parameters.InitialGold,MaxHp=rules.Parameters.DailyHp[0] };
        ApplyTrainerStart(); ActivateInventory(Trigger.DayStart); RollShop(); DrainPendingChoices();State.RandomState=_random.State; return GetObservation();
    }
    public SimulationTimings Timings {get;}=new();
    public Observation GetObservation()
    {
        using var copyTiming=Timings.Copy();
        if(!State.PendingShopStart)RefreshShop();
        return new(State.Day,State.Lives,State.Wins,State.Gold,State.MaxHp,State.Rank,
        State.Trainer,State.Units.Select(x=>x?.Clone()).ToArray(),State.PendingShopStart?new Offer?[5]:State.Shop.Select(x=>x is null?null:x with { Creature=x.Creature?.Clone() }).ToArray(),
        [..State.Bag],[..State.ExtraTrainers],State.Locked,State.FreeRerolls,State.ItemUses,State.MaximumItemUses,State.Phase,
        State.Choices.Select(x=>x with { Effects=[..x.Effects] }).ToArray(),State.Overflow?.Clone(),new(State.Counters),VisibleState());
    }
    public SimAction[] GetLegalActions()
    {
        using var legalTiming=Timings.Legal();
        if(!State.PendingShopStart)RefreshShop();
        if(State.Phase==Phase.Complete) return [];
        if(State.Phase is Phase.Reward or Phase.Event or Phase.TrainerChoice)
        {
            var choices=new List<SimAction>();
            for(var choice=0;choice<State.Choices.Length;choice++)
            {
                var definition=State.Choices[choice];
                if(definition.Effects.Any(x=>x.Operation==Operation.GrantTrinket && x.DefinitionId is { } id && _trinkets[id].Unique && State.Bag.Any(b=>b.Id==id)))continue;
                if(definition.TargetRule is null)choices.Add(new(Move.Choose,Choice:choice));
                else for(var target=0;target<State.Units.Length;target++)if(State.Units[target] is { } unit && ChoiceTarget(unit,definition.TargetRule))choices.Add(new(Move.Choose,Target:target,Choice:choice));
            }
            return choices.ToArray();
        }
        var actions=new List<SimAction>();
        for(var i=0;i<10;i++) if(State.Units[i] is { } source)
        {
            actions.Add(new(Move.Sell,i));
            for(var j=0;j<10;j++) if(i!=j)
            {
                var target=State.Units[j];
                // Occupied swaps are symmetric; publish exactly one action for each result.
                if(target is null || i<j)actions.Add(new(Move.Move,i,j));
            }
        }
        if(State.Phase==Phase.Overflow)
        {
            actions.Add(new(Move.Sell,10));
            for(var i=0;i<10;i++) if(State.Units[i] is null) actions.Add(new(Move.Move,10,i));
            return actions.ToArray();
        }
        for(var i=0;i<5;i++) if(State.Shop[i] is { } offer && offer.Price<=State.Gold)
        {
            if(offer.Creature is not null)
            {
                for(var j=0;j<10;j++) if(State.Units[j] is null) actions.Add(new(Move.Buy,i,j));
                if(State.Units.All(x=>x is not null)) actions.Add(new(Move.Buy,i));
            }
            else if(State.ItemUses>0 && offer.ItemId is not null) actions.Add(new(Move.BuyItem,i));
        }
        if(State.FreeRerolls>0 || State.Gold>=3) actions.Add(new(Move.Reroll));
        actions.Add(new(Move.Lock)); actions.Add(new(Move.Finish));
        return actions.ToArray();
    }
    private long _revision;
    public sealed class ActionPublication
    {
        internal GameEnvironment Owner {get;} internal long Revision {get;}
        public IReadOnlyList<SimAction> Actions {get;}
        internal ActionPublication(GameEnvironment owner,long revision,SimAction[] actions)
        {Owner=owner;Revision=revision;Actions=Array.AsReadOnly(actions);}
    }
    public ActionPublication PublishActions(Func<SimAction[],SimAction[]> filter)
    {
        var legal=GetLegalActions();var selected=filter(legal);var allowed=legal.ToHashSet();
        if(selected.Length==0 || selected.Any(x=>!allowed.Contains(x)))throw new ArgumentException("Invalid published candidates.");
        return new(this,_revision,[..selected]);
    }
    public StepResult StepPublished(ActionPublication publication,int index,Func<FrozenBoard,FrozenBoard>? opponent=null,ulong? battleSeed=null)
    {
        if(publication.Owner!=this || publication.Revision!=_revision || index<0 || index>=publication.Actions.Count)
            throw new ArgumentException("Stale or invalid action publication.");
        return StepCore(publication.Actions[index],opponent,battleSeed);
    }
    public StepResult Step(SimAction action,Func<FrozenBoard,FrozenBoard>? opponent=null,ulong? battleSeed=null)
    {
        if(!GetLegalActions().Contains(action)) throw new ArgumentException("Illegal action.");
        return StepCore(action,opponent,battleSeed);
    }
    private StepResult StepCore(SimAction action,Func<FrozenBoard,FrozenBoard>? opponent,ulong? battleSeed)
    {
        _revision++;
        BattleResult? battle=null; DayRecord? day=null;
        switch(action.Kind)
        {
            case Move.Buy:
                var offer=State.Shop[action.Source]!; State.Gold-=offer.Price; State.Shop[action.Source]=null;
                var bought=offer.Creature!; ConsumeFreePurchase(bought);ClearOfferMarkers(bought);PlaceUnit(bought,action.Target);
                ApplyPurchaseBuffs(bought);ActivateInventory(Trigger.Bought,bought);
                // Bought abilities use the printed level before automatic merging changes it.
                ActivatePersistent(bought,Trigger.Bought);
                foreach(var ally in State.Units.OfType<Unit>().Where(x=>x.Instance!=bought.Instance).ToArray()) ActivatePersistent(ally,Trigger.AllyBought,bought);
                MergeAutomatic();
                break;
            case Move.BuyItem:
                var item=State.Shop[action.Source]!; State.Gold-=item.Price; State.ItemUses--; State.Shop[action.Source]=null;
                UseItem(item.ItemId!,item.Price); break;
            case Move.Sell:
                var sold=action.Source==10?State.Overflow!:State.Units[action.Source]!;
                State.Gold+=SellValue(sold);
                if(action.Source==10) { State.Overflow=null; DrainPendingUnits(); } else State.Units[action.Source]=null;
                State.PendingPurchaseBuffs=State.PendingPurchaseBuffs.Where(x=>x.SourceInstance!=sold.Instance).ToArray();
                break;
            case Move.Move:
                if(action.Source==10) { State.Units[action.Target]=State.Overflow; State.Overflow=null; DrainPendingUnits(); MergeAutomatic(); }
                else { (State.Units[action.Source],State.Units[action.Target])=(State.Units[action.Target],State.Units[action.Source]); MergeAutomatic(); }
                break;
            case Move.Reroll:
                if(State.FreeRerolls>0) State.FreeRerolls--; else State.Gold-=3;
                State.CarryShop|=State.Locked; State.Locked=false; RollShop(); break;
            case Move.Lock: State.Locked=!State.Locked; State.CarryShop=State.Locked; break;
            case Move.Choose:
                var choice=State.Choices[action.Choice];
                var targetUnit=action.Target>=0?State.Units[action.Target]:null;
                var selection=(State.CurrentEvent is { } current?current+": ":"")+choice.Label+(targetUnit is null?"":$" → slot {action.Target+1} ({targetUnit.SpeciesId})");
                State.DayChoices=State.DayChoices.Append(selection).ToArray();
                if(State.Phase==Phase.Reward && choice.DefinitionId is { } trinket) {
                    foreach(var selected in State.TakeAllChoices?State.Choices:[choice])if(selected.DefinitionId is { } rewardId)GainTrinket(rewardId,true);
                }
                else if(State.Phase==Phase.TrainerChoice && choice.DefinitionId is { } trainer) AcquireTrainer(trainer);
                else if(choice.AtShopStart)State.PendingShopEffects=State.PendingShopEffects.Concat(choice.Effects).ToArray();
                else foreach(var effect in choice.Effects) PersistentEffect(effect.Target==Target.Selected?targetUnit:null,effect.Target==Target.Selected?effect with {Target=Target.Self}:effect);
                State.Choices=[];State.TakeAllChoices=false; DrainPendingChoices(); break;
            case Move.Finish:
                if(opponent is null) throw new ArgumentNullException(nameof(opponent));
                ActivateInventory(Trigger.DayEnd);var before=GetObservation(); var own=FrozenBoard.From(State); var enemy=opponent(own);
                var randomBattleSeed=_random.Next();using(Timings.Combat())battle=_combat.ResolveBattle(own,enemy,battleSeed??randomBattleSeed);
                for(var i=0;i<6;i++) State.Units[i]=battle.PersistentTeam[i]?.Clone();
                State.PendingPurchaseBuffs=State.PendingPurchaseBuffs.Concat(battle.PendingPurchaseBuffs??[])
                    .Where(x=>State.Units.Any(u=>u?.Instance==x.SourceInstance)).ToArray();
                State.Bag=[..battle.Bag]; State.Gold+=battle.Gold;
                if(battle.PersistentCounters is not null)State.Counters=new(battle.PersistentCounters);
                EnsureInventoryInstances();
                foreach(var gained in State.Bag.Where(x=>!own.Bag.Any(b=>b.Instance==x.Instance)).ToArray())
                {
                    ApplyInventoryAcquisition(gained);
                    for(var copy=0;copy<gained.Quantity;copy++)foreach(var bench in State.Units.Skip(6).OfType<Unit>())ActivatePersistent(bench,Trigger.TrinketGained);
                }
                if(battle.PlayerWon) { State.Wins++; State.Gold+=State.Counters.GetValueOrDefault("next-victory-gold"); }
                else {var lost=State.Day<=2?1:State.Day<=4?2:3;AddCounter("lives-lost",Math.Min(State.Lives,lost));State.Lives-=lost;}
                State.Counters.Remove("next-victory-gold"); State.Counters.Remove("next-battle-start-repeat");
                var copyCount=(int)State.Counters.GetValueOrDefault("next-copy-enemy"); State.Counters.Remove("next-copy-enemy");
                if(enemy.Team[1] is { } copied) for(var copy=0;copy<copyCount;copy++)
                { var recruited=copied.Clone(); recruited.Instance=State.NextInstance++; PlaceUnit(recruited); }
                var secondChance=State.Lives<=0 && !State.SecondChanceUsed;
                if(secondChance) { State.SecondChanceUsed=true; State.Lives=1; }
                else State.Lives=Math.Max(0,State.Lives);
                day=new(State.Day,before,enemy,battle,State.Wins,Math.Max(0,State.Lives),[..State.DayChoices]);
                if(State.Wins>=rules.Parameters.WinTarget || State.Lives<=0) State.Phase=Phase.Complete;
                else NextDay(secondChance);
                break;
        }
        if(State.Phase!=Phase.Complete) DrainPendingChoices();
        State.RandomState=_random.State;
        return new(GetObservation(),State.Phase==Phase.Complete && State.Wins>=rules.Parameters.WinTarget?1:0,
            State.Phase==Phase.Complete,battle,day);
    }
    public double SellValue(Unit unit) => unit.Counters.ContainsKey("zero-sell")?0: Math.Ceiling((_species[unit.SpeciesId].Cost*(unit.Level switch {1=>1,2=>3,_=>6})+unit.StoredDiscount)/2)+unit.Bonuses[(int)Stat.SellValue];
    private Unit Create(Species definition,int level=1,bool? shiny=null,double? chance=null) => new()
    { Instance=State.NextInstance++,SpeciesId=definition.Id,Level=level,Counters=new() {["born-day"]=State.Day},Shiny=shiny??(definition.Shiny.Length>0 && _random.Uniform()<(chance??Math.Min(1,.005*TraitProduct(ShopTrait.ShinyMultiplier)))) };
    private Species RollSpecies(int rarity)
    {
        var maxed=State.Units.OfType<Unit>().Where(x=>x.Level>=3 && !LevelRules.Unlimited(_species[x.SpeciesId])).Select(x=>x.SpeciesId).ToHashSet();
        for(var tier=rarity;tier>=0;tier--)
        {
            var pool=rules.Species.Where(x=>(x.ShopEligible && x.Pantra||State.ForeignPool.Contains(x.Id)) && x.Rarity==tier && !maxed.Contains(x.Id) &&
                !EvolvesIntoMaxed(x,maxed)).ToArray();
            if(pool.Length>0) return pool[_random.Index(pool.Length)];
        }
        throw new InvalidOperationException("No eligible species at or below rolled rarity.");
    }
    private bool EvolvesIntoMaxed(Species species,HashSet<string> maxed)
    {
        var seen=new HashSet<string>();
        while(species.EvolutionId is { } next && seen.Add(next))
        {if(maxed.Contains(next))return true;if(!_species.TryGetValue(next,out species!))break;}
        return false;
    }
    public double[] GetShopOdds()
    {
        var weights=ShopRankOdds.ForRank(State.Rank).Select(x=>(double)x.Probability*100).ToArray();
        var effects=InventoryEffects().Where(x=>x.Effect.Operation==Operation.ShopModifier).ToArray();
        weights[4]+=effects.Where(x=>x.Effect.ShopTrait==ShopTrait.LegendaryWeight).Sum(x=>x.Effect.Amount);
        var minimum=(int)effects.Where(x=>x.Effect.ShopTrait==ShopTrait.MinimumRarity)
            .Select(x=>x.Effect.Amount).DefaultIfEmpty(0).Max();
        for(var tier=0;tier<Math.Min(minimum,weights.Length);tier++)weights[tier]=0;
        if(weights.Sum()==0)weights[Math.Clamp(minimum,0,4)]=100;
        var sum=weights.Sum();return weights.Select(x=>x/sum).ToArray();
    }
    private int RarityRoll(double[] odds)
    {
        var roll=_random.Uniform();
        for(var i=0;i<odds.Length;i++){roll-=odds[i];if(roll<0)return i;}return odds.Length-1;
    }
    private void RollShop(int? rarity=null,bool fillOnly=false)
    {
        if(!fillOnly)
        {
            var discarded=new double[6];
            foreach(var unit in State.Shop.Select(x=>x?.Creature).OfType<Unit>())
            {var stats=UnitStats(unit);for(var stat=0;stat<6;stat++)discarded[stat]+=stats[stat];}
            foreach(var unit in State.Units.OfType<Unit>().ToArray())ActivatePersistent(unit,Trigger.ShopDiscard,discarded:discarded);
        }
        var forcedTier=PrepareAdvancedShop(rarity);
        var odds=GetShopOdds();var itemBonus=(int)TraitSum(ShopTrait.ItemTierBonus);var noItems=TraitSum(ShopTrait.NoItems)>0;
        var shinyChance=Math.Min(1,.005*TraitProduct(ShopTrait.ShinyMultiplier));
        for(var i=0;i<5;i++)
        {
            if(fillOnly && State.Shop[i] is not null) continue;
            var tier=rarity??forcedTier??RarityRoll(odds);
            var itemTier=Math.Min(5,tier+itemBonus);
            var mythicalChance=TraitSum(ShopTrait.MythicalItemChance);
            if(i==4 && rarity is null && State.ItemUses>0 && !noItems && mythicalChance>0 && _random.Uniform()<Math.Min(1,mythicalChance))itemTier=5;
            var items=rules.Items.Where(x=>x.Rarity==itemTier).ToArray();
            var forcedBerry=i==4 && rarity is null && State.ItemUses>0 && !noItems && State.Counters.GetValueOrDefault("forced-berries")>0;
            if(forcedBerry)
            {
                var berries=rules.Items.Where(x=>x.Id.EndsWith("-berry",StringComparison.Ordinal)).ToArray();
                var berryTier=berries.Where(x=>x.Rarity<=itemTier).Select(x=>x.Rarity).DefaultIfEmpty(berries.Select(x=>x.Rarity).DefaultIfEmpty(-1).Min()).Max();
                items=berries.Where(x=>x.Rarity==berryTier).ToArray();
                if(items.Length==0)throw new InvalidOperationException("No eligible berry item.");
            }
            if(i==4 && rarity is null && State.ItemUses>0 && !noItems && items.Length>0)
            { var item=items[_random.Index(items.Length)]; State.Shop[i]=new(null,item.Id,item.Cost,item.Cost);if(forcedBerry)AddCounter("forced-berries",-1); }
            else { var definition=RollSpecies(i==0 && ConsumeMythicalShop()?5:tier); State.Shop[i]=new(Create(definition,chance:shinyChance),null,definition.Cost); }
        }
        RefreshShop();FinishAdvancedShop();
        State.RandomState=_random.State;
    }
    private void MergeInto(int source,int target)
    {
        var a=source==10?State.Overflow!:State.Units[source]!; var b=State.Units[target]!;
        State.PendingPurchaseBuffs=State.PendingPurchaseBuffs.Select(x=>x.SourceInstance==a.Instance?x with {SourceInstance=b.Instance}:x).ToArray();
        b.Copies+=a.Copies; b.Shiny|=a.Shiny; b.StoredDiscount+=a.StoredDiscount;
        b.AddedTypes=b.AddedTypes.Concat(a.AddedTypes).Distinct().ToArray();
        b.Devoured??=a.Devoured?.Clone();
        foreach(var (key,value) in a.Counters.Where(x=>!x.Key.StartsWith("offer:",StringComparison.Ordinal)))
            b.Counters[key]=b.Counters.TryGetValue(key,out var old)?key=="born-day"?Math.Min(old,value):Math.Max(old,value):value;
        for(var stat=0;stat<9;stat++) b.Bonuses[stat]=Math.Max(b.Bonuses[stat],a.Bonuses[stat]);
        if(source==10) State.Overflow=null; else State.Units[source]=null;
        if(b.Copies>=MergeCopies(b))PromoteCopies(b);
    }
    private void PromoteCopies(Unit b)
    {
        var needed=MergeCopies(b);b.Copies-=needed-1;b.Level++;Evolve(b);var mergedLevel=b.Level;
        ActivateInventory(Trigger.AllyLevelUp,b);
        foreach(var ally in State.Units.OfType<Unit>().Where(x=>x.Instance!=b.Instance).ToArray())ActivatePersistent(ally,Trigger.AllyLevelUp,b);
        NotifyLevelUp(b);
        if(mergedLevel==3 && HasTrainer("twins")){var twin=b.Clone();twin.Instance=State.NextInstance++;twin.Counters["zero-sell"]=1;PlaceUnit(twin);}
        Reward(rules.Profile?.SharedRarityOdds==true?null:_species[b.SpeciesId].Rarity);
    }
    private void MergeAutomatic()
    {
        bool changed;
        do
        {
            changed=false;
            for(var i=0;i<10;i++) if(State.Units[i] is { } a && CanMerge(a))
            {
                var matches=Enumerable.Range(i+1,10-i).Where(j=>(j==10?State.Overflow:State.Units[j]) is { } b &&
                    a.Level==b.Level && _species[a.SpeciesId].MergeGroup==_species[b.SpeciesId].MergeGroup).ToArray();
                var needed=MergeCopies(a);var originalLevel=a.Level;
                if(a.Copies>=needed){PromoteCopies(a);changed=true;break;}
                if(a.Copies+matches.Sum(j=>(j==10?State.Overflow:State.Units[j])!.Copies)<needed)continue;
                foreach(var j in matches) {MergeInto(j,i);if(a.Level>originalLevel)break;}
                changed=true; break;
            }
        } while(changed);
        DrainPendingUnits();
    }
    private void Evolve(Unit unit)
    {
        var species=_species[unit.SpeciesId];
        if(species.EvolutionId is { } target && species.EvolutionLevel is { } level && unit.Level>=level) unit.SpeciesId=target;
    }
    private void Reward(int? rarity=null)
    {
        if(rules.Trinkets.Length==0 && rules.Build=="fixture")return;
        var choices=new List<Choice>();
        var odds=GetShopOdds();
        var count=HasTrainer("burglar")?2:3;
        var bonus=CountTrinket("treasure-map")+CountTrainer("treasure-hunter");
        ConsumeTrinket("treasure-map");
        var takeAll=HasTrainer("burglar") || CountTrinket("greedy-gloves")>0;
        if(CountTrinket("greedy-gloves")>0)ConsumeTrinket("greedy-gloves",1);
        while(choices.Count<count)
        {
            var tier=Math.Min(5,(rarity??RarityRoll(odds))+bonus);InventoryDefinition[] eligible=[];
            while(tier>=0 && eligible.Length==0)
            {eligible=rules.Trinkets.Where(x=>x.Rarity==tier && (!x.Unique||!State.Bag.Any(b=>b.Id==x.Id) && !choices.Any(c=>c.DefinitionId==x.Id))).ToArray();tier--;}
            if(eligible.Length==0)break;
            var entry=eligible[_random.Index(eligible.Length)];choices.Add(new(entry.Name,entry.Id,entry.Effects));
        }
        if(choices.Count==0)throw new InvalidOperationException("Reward pool has no eligible trinket.");
        QueueChoices(Phase.Reward,choices.ToArray(),takeAll:takeAll);
    }
    private bool CanMerge(Unit unit) => unit.Level<3 || LevelRules.Unlimited(_species[unit.SpeciesId]);
    private int MergeCopies(Unit unit)
    {
        var own=Definition(unit).Effects.Where(x=>x.Operation==Operation.MergeRequirement);
        var inventory=InventoryEffects().Select(x=>x.Effect).Where(x=>x.Operation==Operation.MergeRequirement && Matches(unit,x));
        if(CountTrinket("grow-lamp")>0 && Types(unit).Contains("grass"))return 2;
        return Math.Max(2,(int)own.Concat(inventory).Select(x=>x.Amount).Append(unit.Level==1?3:2).Min());
    }
    private void GainTrinket(string id,bool gift=false)
    {
        if(rules.Trinkets.Single(x=>x.Id==id).Unique && State.Bag.Any(x=>x.Id==id))return;
        EnsureInventoryInstances();
        var quantity=1;
        if(gift && !_trinkets[id].Unique){
            quantity+=CountTrinket("ultra-duplicator")+(_trinkets[id].Rarity<=1?CountTrainer("scavenger"):0);
            foreach(var duplicator in State.Bag.Where(x=>x.Id is "mini-duplicator" or "mega-duplicator").ToArray()){
                quantity+=duplicator.Quantity*(duplicator.Id=="mega-duplicator"?2:1);ConsumeTrinket(duplicator.Id);
            }
        }
        var entry=new BagEntry(id,quantity,State.NextInventoryInstance++);State.Bag=State.Bag.Append(entry).ToArray();
        ApplyInventoryAcquisition(entry);RefreshShop();
        for(var copy=0;copy<quantity;copy++)foreach(var unit in State.Units.OfType<Unit>().ToArray()) ActivatePersistent(unit,Trigger.TrinketGained);
    }
    private void ActivatePersistent(Unit unit,Trigger trigger,Unit? actor=null,double[]? discarded=null,bool? shopPhase=null)
    {
        var species=_species[unit.SpeciesId]; var definition=Definition(unit);
        var repeats=trigger==Trigger.Bought?1+(int)TraitSum(ShopTrait.RepeatBought):1;
        for(var repeat=0;repeat<repeats;repeat++)
            foreach(var effect in definition.Effects.Where(x=>x.Trigger==trigger && (shopPhase is null || (x.Target==Target.Shop)==shopPhase) && (x.EventType is null || x.Operation==Operation.PendingPurchase || actor is not null &&
                _species[actor.SpeciesId].Types.Concat(actor.AddedTypes).Contains(x.EventType))))PersistentEffect(unit,effect,discarded);
    }
    private void PersistentEffect(Unit? source,Effect effect,double[]? discarded=null)
    {
        if(effect.Scale!=EffectScale.None)effect=effect with {Amount=effect.Amount*EffectScaling.Factor(effect,source,State.Units.Take(6).OfType<Unit>(),State.Bag,
            _species,UnitStats,lostLives:State.Counters.GetValueOrDefault("lives-lost"),trinkets:_trinkets,wins:State.Wins,
            itemsUsed:State.Counters.GetValueOrDefault("items-used-rarity-"+effect.ScaleRarity)),Scale=EffectScale.None};
        switch(effect.Operation)
        {
            case Operation.InventoryRule: ApplyInventoryRule(source,effect.DefinitionId!);return;
            case Operation.TrainerRule: ApplyTrainerRule(source,effect.DefinitionId!);return;
            case Operation.PendingPurchase:
                if(source is null)throw new InvalidOperationException("A contingent purchase buff needs a source creature.");
                State.PendingPurchaseBuffs=State.PendingPurchaseBuffs.Append(new(source.Instance,effect)).ToArray();return;
            case Operation.ForceBerryItems:AddCounter("forced-berries",effect.Amount);return;
            case Operation.Devour:
                if(source is null)throw new InvalidOperationException("Devouring needs a source creature.");
                if(source.Counters.GetValueOrDefault("born-day")==State.Day)return;
                var actorSlot=Array.IndexOf(State.Units,source);var front=actorSlot is >=3 and <6?actorSlot-3:-1;
                if(front<0 || State.Units[front] is not { } devoured)return;
                source.Devoured=devoured.Clone();State.Units[front]=null;source.SpeciesId=effect.DefinitionId??throw new InvalidDataException("Devour evolution target missing.");return;
            case Operation.ProgressEvolution:
                if(source is null)throw new InvalidOperationException("Evolution progress requires a source creature.");
                var key=EvolutionRules.Counter(source,effect);source.Counters[key]=source.Counters.GetValueOrDefault(key)+1;
                if(source.Counters[key]>=effect.Amount && !effect.DeferredEvolution)PersistentEffect(source,effect with {Operation=Operation.Evolve,ProgressCounter=null});return;
            case Operation.Evolve:
                if(source is null)throw new InvalidOperationException("Evolution requires a source creature.");
                var next=_species[source.SpeciesId].EvolutionId??throw new InvalidDataException("Evolution target missing.");
                if(effect.ProgressCounter is not null && source.Counters.GetValueOrDefault(EvolutionRules.Counter(source,effect))<effect.Amount)return;
                if(effect.RequireNoOwnedEvolution && State.Units.Any(x=>x?.SpeciesId==next))return;
                source.SpeciesId=next;return;
            case Operation.Hatch:
                if(source is null)throw new InvalidOperationException("Hatching requires a source creature.");
                if(source.Counters.ContainsKey("trainer-hatch-day") || source.Counters.GetValueOrDefault("born-day")==State.Day)return;
                var hatchKey="hatch:"+source.SpeciesId;source.Counters[hatchKey]=source.Counters.GetValueOrDefault(hatchKey)+1;
                if(source.Counters[hatchKey]<effect.Amount)return;
                var hatchPool=rules.Species.Where(x=>(x.ShopEligible && x.Pantra || State.ForeignPool.Contains(x.Id)) && x.Rarity==effect.Rarity &&
                    (effect.Type is null || x.Types.Contains(effect.Type))).ToArray();
                if(hatchPool.Length==0)throw new InvalidOperationException("No eligible hatch reward.");
                source.SpeciesId=hatchPool[_random.Index(hatchPool.Length)].Id;source.Level=effect.MinimumLevel??1;return;
            case Operation.Gold: State.Gold+=effect.Amount; return;
            case Operation.ShopRank: State.Rank=Math.Max(1,State.Rank+(int)effect.Amount);return;
            case Operation.FreeReroll: State.FreeRerolls+=(int)effect.Amount;return;
            case Operation.ItemUses: State.ItemUses+=(int)effect.Amount;return;
            case Operation.RollShop: RollShop(effect.Rarity??throw new InvalidDataException("Ticket rarity missing."));return;
            case Operation.TrinketGift: Reward(effect.Rarity??0);return;
            case Operation.GrantTrinket:
                var eligible=rules.Trinkets.Where(x=>(effect.DefinitionId is null||x.Id==effect.DefinitionId) &&
                    (effect.Rarity is null||x.Rarity==effect.Rarity) && (!effect.NonUnique||!x.Unique) && (!x.Unique||!State.Bag.Any(b=>b.Id==x.Id))).ToArray();
                if(eligible.Length==0)throw new InvalidOperationException("No eligible trinket for grant.");
                for(var copy=0;copy<Math.Max(1,(int)effect.Amount);copy++)GainTrinket(eligible[_random.Index(eligible.Length)].Id);return;
            case Operation.GrantUnit:
                var species=effect.DefinitionId is { } fixedId?_species[fixedId]:effect.Foreign?RollForeign(effect.Rarity??0):RollSpecies(effect.Rarity??0);
                var recruited=Create(species,effect.MinimumLevel??1,shiny:effect.DefinitionId is not null?effect.ForceShiny:null);
                recruited.Bonuses[(int)effect.Stat]+=effect.Amount;
                if(effect.AdmitToShop)State.ForeignPool=State.ForeignPool.Append(species.Id).Distinct().ToArray();
                PlaceUnit(recruited);MergeAutomatic();return;
            case Operation.RecruitOffer:
                var offers=Enumerable.Range(0,5).Where(i=>State.Shop[i]?.Creature is not null).ToArray();
                if(offers.Length==0)return;
                var index=offers[_random.Index(offers.Length)];var grant=State.Shop[index]!.Creature!;
                State.Shop[index]=null;ClearOfferMarkers(grant);PlaceUnit(grant);MergeAutomatic();return;
            case Operation.DiscountShop:
                for(var i=0;i<5;i++)if(State.Shop[i] is {Creature:not null} offer)
                {var discount=Math.Min(offer.Price,effect.Amount);State.Shop[i]=offer with {BasisPrice=(offer.BasisPrice??offer.Price)-discount};}
                RefreshShop();return;
            case Operation.NextBattleStartRepeat: AddCounter("next-battle-start-repeat",effect.Amount);return;
            case Operation.NextVictoryGold: AddCounter("next-victory-gold",effect.Amount);return;
            case Operation.NextCopyEnemy: AddCounter("next-copy-enemy",effect.Amount);return;
            case Operation.LoseLife:
                AddCounter("lives-lost",Math.Min(State.Lives,effect.Amount));
                State.Lives-=(int)effect.Amount;
                if(State.Lives<=0) {if(State.SecondChanceUsed){State.Lives=0;State.Phase=Phase.Complete;}else {State.Lives=1;State.SecondChanceUsed=true;SecondChance();}}
                return;
            case Operation.DiscardStats:
                if(source is null||discarded is null)throw new InvalidOperationException("Discard stats requires the source and discarded shop snapshot.");
                for(var stat=0;stat<6;stat++)source.Bonuses[stat]+=discarded[stat]*effect.Amount;return;
            case Operation.Recombine:
                foreach(var unit in State.Units.OfType<Unit>().ToArray())
                {
                    var tier=Math.Min(5,_species[unit.SpeciesId].Rarity+(int)effect.Amount);
                    var pool=rules.Species.Where(x=>x.Rarity==tier && (x.ShopEligible && x.Pantra||State.ForeignPool.Contains(x.Id))).ToArray();
                    if(pool.Length==0)throw new InvalidOperationException("No eligible transformation at rarity "+tier);
                    unit.SpeciesId=pool[_random.Index(pool.Length)].Id;
                }
                return;
        }
        var sourceSlot=source is null?0:Array.IndexOf(State.Units,source);
        IEnumerable<Unit> recipients=effect.Target is Target.Shop or Target.ShopAndTeam or Target.ShopAndAll?
            State.Shop.Select(x=>x?.Creature).OfType<Unit>().Concat(effect.Target==Target.Shop?[]:State.Units.Take(effect.Target==Target.ShopAndTeam?6:10).OfType<Unit>()):
            effect.Target==Target.Self && source is not null?[source]:
            (effect.Target is Target.All or Target.RandomAlly?State.Units.OfType<Unit>():
                (sourceSlot is >=0 and <6?Combat.Targets(sourceSlot,effect.Target):[]).Select(i=>State.Units[i]).OfType<Unit>());
        if(effect.IncludeSelf && source is not null)recipients=recipients.Append(source).Distinct();
        recipients=recipients.Where(unit=>effect.IncludeSelf && ReferenceEquals(unit,source) || Matches(unit,effect));
        if(effect.Operation==Operation.Shiny)recipients=recipients.Where(x=>!x.Shiny && _species[x.SpeciesId].Shiny.Length>0);
        var selected=recipients.ToList();
        if(effect.Target==Target.RandomAlly)
        {
            var sample=new List<Unit>();while(sample.Count<effect.TargetCount && selected.Count>0)
            {var i=_random.Index(selected.Count);sample.Add(selected[i]);selected.RemoveAt(i);}selected=sample;
        }
        foreach(var unit in selected)
            switch(effect.Operation)
            {
                case Operation.Add: if(unit.SpeciesId!="zephyrex"||effect.Stat!=Stat.Multicast)unit.Bonuses[(int)effect.Stat]+=FlatBonus(effect.Stat,effect.Amount);break;
                case Operation.Multiply: if(unit.SpeciesId!="zephyrex"||effect.Stat!=Stat.Multicast)unit.Bonuses[(int)effect.Stat]+=UnitStats(unit)[(int)effect.Stat]*effect.Amount;break;
                case Operation.LevelUp:
                    var oldLevel=unit.Level;unit.Level=effect.UnlimitedLevels?checked(unit.Level+1):Math.Min(4,unit.Level+1);unit.Copies=1;Evolve(unit);
                    if(unit.Level!=oldLevel)NotifyLevelUp(unit);break;
                case Operation.Shiny: unit.Shiny=true;break;
                case Operation.AddType: if(effect.Type is not null)unit.AddedTypes=unit.AddedTypes.Append(effect.Type).Distinct().ToArray();break;
                case Operation.Transform:
                    var id=effect.DefinitionId??throw new InvalidDataException("Transformation target missing.");
                    unit.SpeciesId=id.StartsWith("rarity:")?RollSpecies(int.Parse(id[7..])).Id:id;break;
                case Operation.ActivateAbility:
                    for(var repeat=0;repeat<effect.Amount;repeat++)ActivatePersistent(unit,effect.SubjectTrigger);break;
                default: throw new NotSupportedException($"Persistent handler missing: {effect.Operation}");
            }
    }
    private void NotifyLevelUp(Unit actor)
    {
        ActivateInventory(Trigger.AllyAnyLevelUp,actor);
        if(HasTrainer("chemist"))AddCounter("chemist-levels",CountTrainer("chemist"));
        foreach(var ally in State.Units.OfType<Unit>().Where(x=>x.Instance!=actor.Instance).ToArray())ActivatePersistent(ally,Trigger.AllyAnyLevelUp,actor);
    }
    private void UseItem(string id,double paidPrice)
    {
        var definition=rules.Items.Single(x=>x.Id==id);
        var repeats=1+(paidPrice==0?(int)TraitSum(ShopTrait.RepeatZeroItem):0);
        for(var repeat=0;repeat<repeats;repeat++)
        {
            foreach(var effect in definition.Effects)PersistentEffect(null,effect);
            AddCounter("items-used",1);AddCounter("items-used-rarity-"+definition.Rarity,1);
            foreach(var unit in State.Units.OfType<Unit>().ToArray())ActivatePersistent(unit,Trigger.ItemUsed);
        }
        AddCounter("items-bought",1);ActivateInventory(Trigger.ItemBought);
    }
    private void ApplyPurchaseBuffs(Unit bought)
    {
        var remaining=new List<PurchaseBuff>();
        foreach(var pending in State.PendingPurchaseBuffs)
        {
            var effect=pending.Effect;
            if(effect.EventType is not null && !_species[bought.SpeciesId].Types.Concat(bought.AddedTypes).Contains(effect.EventType))
            {remaining.Add(pending);continue;}
            if(effect.Type is not null)PersistentEffect(bought,effect with {Operation=Operation.AddType,Target=Target.Self});
            PersistentEffect(bought,effect with {Operation=Operation.Add,Target=Target.Self,Type=null});
        }
        State.PendingPurchaseBuffs=remaining.ToArray();
    }
    private LevelDefinition Definition(Unit unit) => LevelRules.Resolve(_species[unit.SpeciesId],unit.Level,unit.Shiny);
    private double[] UnitStats(Unit unit) => Definition(unit).Stats
        .Select((value,i)=>value+unit.Bonuses[i]).ToArray();
    private bool Matches(Unit unit,Effect effect) =>
        (effect.Operation==Operation.AddType||effect.Type is null||_species[unit.SpeciesId].Types.Concat(unit.AddedTypes).Contains(effect.Type)) &&
        (effect.Rarity is null||_species[unit.SpeciesId].Rarity==effect.Rarity) &&
        (effect.MaximumRarity is null||_species[unit.SpeciesId].Rarity<=effect.MaximumRarity) &&
        (effect.MinimumLevel is null||unit.Level>=effect.MinimumLevel) && (effect.MaximumLevel is null||unit.Level<=effect.MaximumLevel) &&
        (!effect.RequireNoAbility||Definition(unit).Effects.Length==0);
    private void AddCounter(string name,double amount) => State.Counters[name]=State.Counters.GetValueOrDefault(name)+amount;
    private bool ChoiceTarget(Unit unit,TargetRule rule) =>
        (rule.MinimumLevel is null||unit.Level>=rule.MinimumLevel) && (rule.MaximumLevel is null||unit.Level<=rule.MaximumLevel) &&
        (rule.ExcludedType is null||!_species[unit.SpeciesId].Types.Concat(unit.AddedTypes).Contains(rule.ExcludedType)) &&
        (!rule.NonShiny||!unit.Shiny && _species[unit.SpeciesId].Shiny.Length>0);
    private Species RollForeign(int rarity)
    {
        var pool=rules.Species.Where(x=>x.ForeignEligible && x.Rarity==rarity).ToArray();
        if(pool.Length==0)throw new InvalidOperationException("No eligible foreign-region creature at rarity "+rarity);
        return pool[_random.Index(pool.Length)];
    }
    public EncounterDefinition[] GetEligibleEncounters(int tier) => (rules.Encounters??[]).Where(x=>x.Tier==tier && x.Region=="Pantra" &&
        (rules.Profile?.FullyUnlocked==true || State.Counters.GetValueOrDefault("completed-games")>=x.MinimumGames)).ToArray();
    public void OpenEncounter(string id)
    {
        rules.RequireReady();
        var definition=(rules.Encounters??[]).Single(x=>x.Id==id);
        QueueChoices(Phase.Event,definition.Choices,definition.Name);
        State.RandomState=_random.State;
    }
    private static void ClearOfferMarkers(Unit unit)
    {foreach(var key in unit.Counters.Keys.Where(x=>x.StartsWith("offer:",StringComparison.Ordinal)).ToArray())unit.Counters.Remove(key);}
    private void EnsureInventoryInstances()
    {
        State.NextInventoryInstance=Math.Max(State.NextInventoryInstance,State.Bag.Select(x=>x.Instance).DefaultIfEmpty(0).Max()+1);
        for(var i=0;i<State.Bag.Length;i++)if(State.Bag[i].Instance==0)State.Bag[i]=State.Bag[i] with {Instance=State.NextInventoryInstance++};
    }
    private IEnumerable<(Effect Effect,string Key)> InventoryEffects()
    {
        EnsureInventoryInstances();
        foreach(var entry in State.Bag)
        {
            var effects=_trinkets[entry.Id].Effects;
            for(var copy=0;copy<entry.Quantity;copy++)for(var i=0;i<effects.Length;i++)
                if(State.Day>=effects[i].MinimumDay)yield return(effects[i],$"bag:{entry.Instance}:{copy}:{i}");
        }
        var trainers=State.ExtraTrainers.Prepend(State.Trainer).ToArray();
        for(var trainer=0;trainer<trainers.Length;trainer++)
        {
            var effects=_trainers[trainers[trainer]].Effects;
            for(var i=0;i<effects.Length;i++)if(State.Day>=effects[i].MinimumDay)yield return(effects[i],$"trainer:{trainer}:{i}");
        }
        foreach(var unit in _hasOwnedShopTraits?State.Units.OfType<Unit>():[])
        {
            var effects=Definition(unit).Effects;
            for(var i=0;i<effects.Length;i++)if(effects[i].Operation==Operation.ShopModifier)yield return(effects[i],$"owned:{unit.Instance}:{i}");
        }
    }
    private double TraitSum(ShopTrait trait,Unit? unit=null) => InventoryEffects().Where(x=>x.Effect.Operation==Operation.ShopModifier &&
        x.Effect.ShopTrait==trait && (x.Effect.Type is null||unit is not null && Matches(unit,x.Effect))).Sum(x=>x.Effect.Amount);
    private double TraitProduct(ShopTrait trait) => InventoryEffects().Where(x=>x.Effect.Operation==Operation.ShopModifier && x.Effect.ShopTrait==trait)
        .Aggregate(1d,(value,x)=>value*x.Effect.Amount);
    private void ActivateInventory(Trigger trigger,Unit? target=null)
    {
        foreach(var (effect,_) in InventoryEffects().Where(x=>x.Effect.Trigger==trigger).ToArray())PersistentEffect(target,effect);
    }
    private void ApplyInventoryAcquisition(BagEntry entry)
    {
        var definition=_trinkets[entry.Id];
        for(var copy=0;copy<entry.Quantity;copy++)foreach(var effect in definition.Effects.Where(x=>x.Trigger==Trigger.RunStart))PersistentEffect(null,effect);
        var allowance=definition.Effects.Where(x=>x.Operation==Operation.ShopModifier && x.ShopTrait==ShopTrait.DailyItemUses).Sum(x=>x.Amount)*entry.Quantity;
        State.MaximumItemUses+=(int)allowance;State.ItemUses+=(int)allowance;
    }
    private void ConsumeFreePurchase(Unit unit)
    {
        // Current 1.3.0 priority: a Common Bug consumes Bargain Bin first, leaving Bug Catcher armed.
        if(_species[unit.SpeciesId].Rarity==0 && State.Counters.GetValueOrDefault("free-common-used")<TraitSum(ShopTrait.FirstCommonFree))AddCounter("free-common-used",1);
        else if(_species[unit.SpeciesId].Types.Concat(unit.AddedTypes).Contains("bug") && State.Counters.GetValueOrDefault("free-bug-used")<TraitSum(ShopTrait.FirstBugFree))AddCounter("free-bug-used",1);
    }
    private void RefreshShop()
    {
        var effects=InventoryEffects().ToArray();
        double Sum(ShopTrait trait,Unit? unit=null) => effects.Where(x=>x.Effect.Operation==Operation.ShopModifier &&
            x.Effect.ShopTrait==trait && (x.Effect.Type is null||unit is not null && Matches(unit,x.Effect))).Sum(x=>x.Effect.Amount);
        var forcedShiny=effects.Any(x=>x.Effect.Operation==Operation.ShopModifier && x.Effect.ShopTrait==ShopTrait.ForcedShiny);
        var itemDiscount=effects.Where(x=>x.Effect.Operation==Operation.ShopModifier && x.Effect.ShopTrait==ShopTrait.ItemDiscount)
            .Aggregate(1d,(value,x)=>value*(1-x.Effect.Amount));
        for(var i=0;i<State.Shop.Length;i++)if(State.Shop[i] is { } offer)
        {
            var basis=offer.BasisPrice??offer.Price;
            if(offer.Creature is not { } unit)
            {
                var itemPrice=basis*itemDiscount;
                itemPrice=rules.Profile?.PriceRounding switch {"floor"=>Math.Floor(itemPrice),"ceil"=>Math.Ceiling(itemPrice),_=>itemPrice};
                State.Shop[i]=offer with {BasisPrice=basis,Price=itemPrice};continue;
            }
            if(forcedShiny && _species[unit.SpeciesId].Shiny.Length>0)unit.Shiny=true;
            foreach(var (effect,key) in effects.Where(x=>x.Effect.Trigger==Trigger.ShopSpawn))
            {
                var marker="offer:"+key;if(unit.Counters.ContainsKey(marker)||!Matches(unit,effect))continue;
                PersistentEffect(unit,effect with {Target=Target.Self});unit.Counters[marker]=1;
            }
            ApplyTrainerTyping(unit);
            var price=Math.Max(0,basis-Sum(ShopTrait.CreatureDiscount,unit));
            if(HasTrainer("smuggler") && !_species[unit.SpeciesId].Pantra)price=RoundPrice(price*.75);
            if(_species[unit.SpeciesId].Rarity==0 && State.Counters.GetValueOrDefault("free-common-used")<Sum(ShopTrait.FirstCommonFree) ||
                _species[unit.SpeciesId].Types.Concat(unit.AddedTypes).Contains("bug") && State.Counters.GetValueOrDefault("free-bug-used")<Sum(ShopTrait.FirstBugFree))price=0;
            var discount=Math.Max(0,_species[unit.SpeciesId].Cost-price);var old=unit.Counters.GetValueOrDefault("offer:discount");
            unit.StoredDiscount+=discount-old;unit.Counters["offer:discount"]=discount;
            State.Shop[i]=offer with {BasisPrice=basis,Price=price};
        }
    }
    private void PlaceUnit(Unit unit,int requested=-1)
    {
        ApplyTrainerTyping(unit);
        var slot=requested>=0?requested:Array.FindIndex(State.Units,x=>x is null);
        if(slot>=0)State.Units[slot]=unit;
        else if(State.Overflow is null)State.Overflow=unit;
        else State.PendingUnits=State.PendingUnits.Append(unit).ToArray();
    }
    private void DrainPendingUnits()
    {
        while(State.PendingUnits.Length>0)
        {
            var slot=Array.FindIndex(State.Units,x=>x is null);
            if(slot<0 && State.Overflow is not null)break;
            var unit=State.PendingUnits[0];State.PendingUnits=State.PendingUnits[1..];PlaceUnit(unit,slot);
        }
        if(State.Overflow is not null)State.Phase=Phase.Overflow;
        else if(State.Phase==Phase.Overflow)State.Phase=Phase.Shop;
    }
    private void QueueChoices(Phase phase,Choice[] choices,string? eventName=null,bool takeAll=false)
    {
        var batch=new ChoiceBatch(phase,choices,eventName??State.CurrentEvent,takeAll);
        State.PendingChoices=phase==Phase.Reward && State.Phase is Phase.Event or Phase.TrainerChoice?
            State.PendingChoices.Prepend(batch).ToArray():State.PendingChoices.Append(batch).ToArray();DrainPendingChoices();
    }
    private void DrainPendingChoices()
    {
        DrainPendingUnits();
        if(State.Phase==Phase.Complete || State.Overflow is not null)return;
        if(State.Choices.Length>0) {State.Phase=State.ActiveChoicePhase;return;}
        if(State.PendingChoices.Length>0)
        {
            var next=State.PendingChoices[0];State.PendingChoices=State.PendingChoices[1..];
            State.ActiveChoicePhase=State.Phase=next.Phase;State.Choices=next.Choices;State.CurrentEvent=next.EventName;State.TakeAllChoices=next.TakeAll;
        }
        else
        {
            State.Phase=Phase.Shop;
            if(State.PendingShopStart)StartShop();
            State.CurrentEvent=null;
        }
    }
    private void SecondChance()
    {
        // The explicit diagnostic fixture has no reward catalog. Production manifests must cover the real event.
        if(rules.Build=="fixture" && rules.Trinkets.Length==0)return;
        if((rules.Encounters??[]).Any(x=>x.Id=="events/second-chance")) {OpenEncounter("events/second-chance");return;}
        QueueChoices(Phase.Event,[
        new("Accept a life and a trinket",null,[new(Trigger.Bought,Operation.TrinketGift,Target.All,Rarity:3)]),
        new("Accept a life and a monster",null,[new(Trigger.Bought,Operation.GrantUnit,Target.All,Rarity:3,MinimumLevel:2)]),
        new("Accept a life and a charm",null,[new(Trigger.Bought,Operation.GrantTrinket,Target.All,DefinitionId:"mysterious-charm")])]);
    }
    private void ApplyTrainerStart()
    {
        foreach(var effect in rules.Trainers.Single(x=>x.Id==State.Trainer).Effects.Where(x=>x.Trigger is Trigger.RunStart or Trigger.Bought))PersistentEffect(null,effect);
    }
    private void NextDay(bool secondChance=false)
    {
        State.Day++; State.Rank++; State.DayChoices=[]; State.Phase=Phase.Shop;
        if(State.Day>rules.Parameters.DailyHp.Length || State.Day>rules.Parameters.DailyIncome.Length)
            throw new InvalidOperationException("Run parameter schedule exhausted.");
        State.Counters.Remove("free-common-used");State.Counters.Remove("free-bug-used");State.Counters.Remove("power-band-used");
        State.MaxHp=rules.Parameters.DailyHp[State.Day-1];State.Gold+=rules.Parameters.DailyIncome[State.Day-1]+TraitSum(ShopTrait.Income);
        State.MaximumItemUses=1+(int)TraitSum(ShopTrait.DailyItemUses);State.ItemUses=State.MaximumItemUses;
        ActivateInventory(Trigger.DayStart);
        State.PendingShopStart=true;
        if(secondChance)SecondChance();
        if(rules.Profile?.Events.FirstOrDefault(x=>x.Day==State.Day) is { } schedule)
        {
            var events=GetEligibleEncounters(schedule.Tier);
            if(events.Length==0)throw new InvalidOperationException("No eligible scheduled event for tier "+schedule.Tier);
            OpenEncounter(events[_random.Index(events.Length)].Id);
        }
        DrainPendingChoices();
    }
    private void StartShop()
    {
        State.PendingShopStart=false;
        var queued=State.PendingShopEffects;State.PendingShopEffects=[];
        foreach(var effect in queued)PersistentEffect(null,effect);
        foreach(var unit in State.Units.OfType<Unit>().ToArray())ActivatePersistent(unit,Trigger.DayStart,shopPhase:false);
        RollShop(fillOnly:State.Locked||State.CarryShop);State.Locked=false;State.CarryShop=false;
        foreach(var unit in State.Units.OfType<Unit>().ToArray())ActivatePersistent(unit,Trigger.DayStart,shopPhase:true);
    }
}
