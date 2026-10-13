using BatomonHelper.Core;
namespace BatomonHelper.Simulation;

public sealed partial class GameEnvironment
{
    private int CountTrinket(string id)=>State.Bag.Where(x=>x.Id==id).Sum(x=>x.Quantity);
    private int CountTrainer(string id)=>State.ExtraTrainers.Prepend(State.Trainer).Count(x=>x==id);
    private bool HasTrainer(string id)=>CountTrainer(id)>0;
    private IEnumerable<string> Types(Unit unit)=>_species[unit.SpeciesId].Types.Concat(unit.AddedTypes).Distinct();
    private double RoundPrice(double value)=>rules.Profile?.PriceRounding switch {"ceil"=>Math.Ceiling(value),"fractional"=>value,_=>Math.Floor(value)};
    private double FlatBonus(Stat stat,double value)=>stat is Stat.Multicast or Stat.Speed?value:value*(1+.8*CountTrinket("zenith-stone"));
    private void ConsumeTrinket(string id,int quantity=int.MaxValue)
    {
        var entries=new List<BagEntry>();
        foreach(var entry in State.Bag){var used=entry.Id==id?Math.Min(entry.Quantity,quantity):0;quantity-=used;if(entry.Quantity>used)entries.Add(entry with {Quantity=entry.Quantity-used});}
        State.Bag=entries.ToArray();
    }
    private VisibleRunState VisibleState()=>new([..State.ForeignPool],State.CarryShop,State.SecondChanceUsed,State.PendingUnits.Select(x=>x.Clone()).ToArray(),
        State.TakeAllChoices,[..State.PendingShopEffects],[..State.PendingPurchaseBuffs],State.PendingShopStart,State.ActiveChoicePhase,State.CurrentEvent);
    private void AcquireTrainer(string trainer)
    {
        State.ExtraTrainers=State.ExtraTrainers.Append(trainer).ToArray();
        foreach(var effect in _trainers[trainer].Effects.Where(x=>x.Trigger==Trigger.RunStart))PersistentEffect(null,effect);
        foreach(var effect in _trainers[trainer].Effects.Where(x=>x.Trigger==Trigger.DayStart))PersistentEffect(null,effect);
    }
    private void TrainerChoice()
    {
        var eligible=rules.Trainers.Where(x=>!HasTrainer(x.Id) && x.Id!="masked-man").ToList();
        var choices=new List<Choice>();
        while(eligible.Count>0 && choices.Count<3){var index=_random.Index(eligible.Count);var trainer=eligible[index];eligible.RemoveAt(index);choices.Add(new(trainer.Name,trainer.Id,trainer.Effects));}
        if(choices.Count>0)QueueChoices(Phase.TrainerChoice,choices.ToArray());
    }
    private void ApplyInventoryRule(Unit? target,string id)
    {
        // These handlers execute only on their declared lifecycle trigger; combat handlers live in Combat.
        switch(id)
        {
            case "blue-incense":case "purple-incense":case "gold-incense":AddCounter("incense:"+id,5);return;
            case "mystic-incense":AddCounter("mythical-shop",1);return;
            case "mysterious-mask":ConsumeTrinket(id,1);TrainerChoice();return;
            case "piggy-bank":State.Gold+=Math.Floor(State.Gold/10);return;
            case "power-band":
                if(target is not null && State.Counters.GetValueOrDefault("power-band-used")==0){target.Bonuses[0]+=FlatBonus(Stat.Damage,20*CountTrinket(id));AddCounter("power-band-used",1);}return;
            case "speed-whistle":
                if(target is not null && CountTrinket(id)>0){target.Bonuses[6]+=.12;ConsumeTrinket(id,1);}return;
            case "upgrade-disc":
                if(target is not null && CountTrinket(id)>0){ConsumeTrinket(id,1);PersistentEffect(target,new(Trigger.None,Operation.LevelUp,Target.Self,Permanent:true));}return;
            case "mega-upgrade-disc":
                if(target is {Level:3} && CountTrinket(id)>0){ConsumeTrinket(id,1);PersistentEffect(target,new(Trigger.None,Operation.LevelUp,Target.Self,Permanent:true));}return;
            default:throw new NotSupportedException("Persistent inventory rule "+id);
        }
    }
    private void ApplyTrainerRule(Unit? target,string id)
    {
        switch(id)
        {
            case "egg-breeder":
                if(State.Counters.ContainsKey("egg-breeder-granted"))return;
                State.Counters["egg-breeder-granted"]=1;
                var egg=Create(_species["purple-egg"]);egg.Counters["trainer-hatch-day"]=State.Day+5;PlaceUnit(egg);return;
            case "gamer":
                if(State.Day<9 || State.Counters.ContainsKey("gamer-granted"))return;
                State.Counters["gamer-granted"]=1;State.Gold+=30;PlaceUnit(Create(RollSpecies(5)));return;
            case "mad-scientist":
                if(State.Day<7 || State.Counters.ContainsKey("mad-scientist-used"))return;
                State.Counters["mad-scientist-used"]=1;
                foreach(var unit in State.Units.Take(6).OfType<Unit>().ToArray()){unit.SpeciesId=RollSpecies(4).Id;unit.Level=1;unit.Copies=1;}return;
            case "masked-man":
                if(State.Day%4==0 && State.Counters.GetValueOrDefault("masked-choice-day")!=State.Day){State.Counters["masked-choice-day"]=State.Day;TrainerChoice();}return;
            case "monster-ranger":
                if(!State.Counters.Keys.Any(x=>x.StartsWith("ranger-species:"))){var unit=Create(RollSpecies(1));State.Counters["ranger-species:"+unit.SpeciesId]=State.Day;PlaceUnit(unit);}
                else{var key=State.Counters.Keys.First(x=>x.StartsWith("ranger-species:"));var born=(int)State.Counters[key];if(State.Day>born && (State.Day-born)%2==0)PlaceUnit(Create(_species[key[15..]]));}return;
            case "painter":
                if(State.Counters.Keys.Any(x=>x.StartsWith("paint:")))return;
                var pool=rules.Species.Where(x=>x.ShopEligible && (x.Pantra || HasTrainer("smuggler"))).ToList();
                foreach(var type in rules.Species.SelectMany(x=>x.Types).Distinct().Order())
                {if(pool.Count==0)break;var index=_random.Index(pool.Count);State.Counters["paint:"+pool[index].Id+":"+type]=1;pool.RemoveAt(index);}return;
            case "swim-coach":
                var water=rules.Species.Where(x=>x.ShopEligible && (x.Pantra||State.ForeignPool.Contains(x.Id)) && x.Types.Contains("water")).ToArray();
                var odds=GetShopOdds();var tier=RarityRoll(odds);var eligible=water.Where(x=>x.Rarity==tier).ToArray();
                if(eligible.Length==0)eligible=water.Where(x=>x.Rarity<=tier).ToArray();if(eligible.Length==0)eligible=water;
                if(eligible.Length==0)throw new InvalidDataException("No Water trainer reward.");PlaceUnit(Create(eligible[_random.Index(eligible.Length)]));return;
            default:throw new NotSupportedException("Persistent trainer rule "+id);
        }
    }
    private void ApplyTrainerTyping(Unit unit)
    {
        var prefix="paint:"+unit.SpeciesId+":";
        foreach(var key in State.Counters.Keys.Where(x=>x.StartsWith(prefix)))unit.AddedTypes=unit.AddedTypes.Append(key[prefix.Length..]).Distinct().ToArray();
        if(HasTrainer("pyromaniac") && Types(unit).Count()==1)unit.AddedTypes=unit.AddedTypes.Append("fire").Distinct().ToArray();
    }
    private int? PrepareAdvancedShop(int? ticket)
    {
        if(HasTrainer("smuggler"))State.ForeignPool=State.ForeignPool.Concat(rules.Species.Where(x=>x.ForeignEligible).Select(x=>x.Id)).Distinct().ToArray();
        if(ticket is not null)return null;
        int? forced=null;
        foreach(var id in new[]{"blue-incense","purple-incense","gold-incense"})if(State.Counters.GetValueOrDefault("incense:"+id)>0)
        {forced=id=="blue-incense"?2:id=="purple-incense"?3:4;AddCounter("incense:"+id,-1);}
        if(CountTrinket("rigged-dice")>0 && State.Counters.GetValueOrDefault("rigged-day")!=State.Day)
        {State.Counters["rigged-day"]=State.Day;forced=GetShopOdds().Select((x,i)=>(x,i)).Where(x=>x.x>0).Max(x=>x.i);}
        return forced;
    }
    private bool ConsumeMythicalShop()
    {
        if(State.Counters.GetValueOrDefault("mythical-shop")<=0)return false;
        AddCounter("mythical-shop",-1);ConsumeTrinket("mystic-incense",1);return true;
    }
    private void FinishAdvancedShop()
    {
        foreach(var id in new[]{"blue-incense","purple-incense","gold-incense"})if(State.Counters.TryGetValue("incense:"+id,out var left) && left<=0){ConsumeTrinket(id);State.Counters.Remove("incense:"+id);}
        foreach(var unit in State.Units.OfType<Unit>())
        {
            if(unit.Counters.TryGetValue("trainer-hatch-day",out var hatch) && State.Day>=hatch)
            {unit.SpeciesId=RollSpecies(3).Id;unit.Level=2;unit.Counters.Remove("trainer-hatch-day");}
            ApplyTrainerTyping(unit);
        }
    }
}
