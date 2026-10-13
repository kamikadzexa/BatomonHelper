using System.Text.Json;
using System.Text.Json.Serialization;
namespace BatomonHelper.Simulation;

/// <summary>Versioned, player-visible numeric contract. Random state and future opponents are excluded.</summary>
public sealed class PolicyEncoding
{
    public const int Version=5;
    public const int TokenWidth=64;
    public const int ActionWidth=40;
    private readonly RulesManifest rules;
    private readonly Dictionary<string,int> _ids;
    private readonly Dictionary<string,int> _types;
    private readonly Dictionary<string,Species> _species;
    private readonly Dictionary<string,int> _items;
    private readonly Dictionary<string,int> _trinkets;
    private readonly Dictionary<string,int> _trainers;
    private readonly Dictionary<string,int> _symbols;
    private readonly Dictionary<Effect,float[]> _effects;
    public static readonly System.Reflection.PropertyInfo[] EffectFields=typeof(Effect).GetProperties().OrderBy(x=>x.MetadataToken).ToArray();
    public PolicyEncoding(RulesManifest manifest)
    {
        rules=manifest;_ids=rules.Species.Select((x,i)=>(x.Id,i)).ToDictionary(x=>x.Id,x=>x.i+1);
        _species=rules.Species.ToDictionary(x=>x.Id);_types=rules.Species.SelectMany(x=>x.Types).Distinct().Order().Select((x,i)=>(x,i)).ToDictionary(x=>x.x,x=>x.i);
        _items=rules.Items.Select((x,i)=>(x.Id,i)).ToDictionary(x=>x.Id,x=>x.i+1);
        _trinkets=rules.Trinkets.Select((x,i)=>(x.Id,i)).ToDictionary(x=>x.Id,x=>x.i+1);
        _trainers=rules.Trainers.Select((x,i)=>(x.Id,i)).ToDictionary(x=>x.Id,x=>x.i+1);
        var effects=rules.Species.SelectMany(x=>x.Normal.Concat(x.Shiny)).SelectMany(x=>x.Effects)
            .Concat(rules.Items.Concat(rules.Trinkets).Concat(rules.Trainers).SelectMany(x=>x.Effects))
            .Concat((rules.Encounters??[]).SelectMany(x=>x.Choices).SelectMany(x=>x.Effects)).Distinct().ToArray();
        _symbols=effects.SelectMany(x=>EffectFields.Select(p=>p.GetValue(x))).OfType<string>()
            .Concat(_ids.Keys).Concat(_items.Keys).Concat(_trinkets.Keys).Concat(_trainers.Keys).Concat(_types.Keys)
            .Distinct().Order(StringComparer.Ordinal).Select((x,i)=>(x,i)).ToDictionary(x=>x.x,x=>x.i+1);
        if(EffectFields.Length+4>TokenWidth)throw new InvalidOperationException("Effect descriptor exceeds token width.");
        _effects=effects.ToDictionary(x=>x,Descriptor);
    }
    private static float Number(double value)=>(float)(Math.Sign(value)*Math.Log(1+Math.Abs(value))/10);
    private float Symbol(string? value)=>value is null?0:_symbols.TryGetValue(value,out var id)?id/4096f:StableKey(value);
    private static float StableKey(string key){uint h=2166136261;foreach(var c in key){h^=c;h*=16777619;}return (h&0xFFFFFF)/16777216f;}
    private float[] Descriptor(Effect effect)
    {
        var row=new float[TokenWidth];row[0]=6;
        for(var i=0;i<EffectFields.Length;i++){
            var value=EffectFields[i].GetValue(effect);row[i+4]=value switch {
                null=>-1,string text=>Symbol(text),bool boolean=>boolean?1:0,Enum enumeration=>Convert.ToInt32(enumeration)/64f,
                _=>Number(Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture))};
        }return row;
    }
    public int ObservationWidth => 16+15*18+rules.Trinkets.Length+rules.Trainers.Length;
    public float[] Observation(Observation observation,int decisions=0,int limit=64,ShopTurnMemory? memory=null)
    {
        var result=new float[ObservationWidth]; var n=0;
        foreach(var value in new double[] {observation.Day/20d,observation.TurnsRemaining/Math.Max(1,rules.Parameters.EffectiveTurnCap),observation.Wins/10d,observation.Gold/100d,
            observation.MaxHp/1000d,observation.Rank/14d,observation.FreeRerolls/10d,observation.ItemUses/5d,observation.MaximumItemUses/5d,
            observation.Locked?1:0,(int)observation.Phase/6d,observation.Choices.Length/10d,observation.Overflow is null?0:1,
            observation.Counters?.GetValueOrDefault("next-battle-start-repeat")??0,
            (observation.Counters?.GetValueOrDefault("next-victory-gold")??0)/100,
            observation.Counters?.GetValueOrDefault("next-copy-enemy")??0}) result[n++]=(float)value;
        var slots=observation.Units.Concat(observation.Shop.Select(x=>x?.Creature)).ToArray();
        for(var slot=0;slot<slots.Length;slot++)
        {
            var unit=slots[slot];
            var start=n; if(unit is not null)
            {
                result[n++]=_ids[unit.SpeciesId]; result[n++]=unit.Level/4f; result[n++]=unit.Shiny?1:0; result[n++]=unit.Copies/3f;
                var species=_species[unit.SpeciesId]; var level=LevelRules.Resolve(species,unit.Level,unit.Shiny);
                result[n++]=(float)(level.Cooldown??0)/10;
                for(var i=0;i<9;i++) result[n++]=(float)(level.Stats[i]+unit.Bonuses[i])/(i>=6?10:100);
                result[n++]=species.Rarity/5f;
                result[n++]=unit.AddedTypes.Where(_types.ContainsKey).Sum(x=>1<<_types[x]);
            }
            if(slot>=10 && observation.Shop[slot-10] is { } offer)
            {
                result[start+16]=offer.ItemId is null?0:_items.GetValueOrDefault(offer.ItemId!);
                result[start+17]=(float)offer.Price/100;
            }
            n=start+18;
        }
        foreach(var trinket in rules.Trinkets) result[n++]=observation.Bag.Where(x=>x.Id==trinket.Id).Sum(x=>x.Quantity);
        foreach(var trainer in rules.Trainers) result[n++]=observation.Trainer==trainer.Id || observation.ExtraTrainers.Contains(trainer.Id)?1:0;
        var tokens=Tokens(observation,decisions,limit,memory);
        return result.Concat(new[]{(float)(tokens.Count/TokenWidth)}).Concat(tokens).ToArray();
    }

    public float[] Action(Observation observation,SimAction action)
    {
        var result=new float[ActionWidth];
        Action(observation,action,result);
        return result;
    }
    public void Action(Observation observation,SimAction action,Span<float> result)
    {
        if(result.Length<ActionWidth)throw new ArgumentException("Action feature destination is too small.",nameof(result));
        result[..ActionWidth].Clear();result[(int)action.Kind]=1;
        result[9]=(action.Source+1)/11f; result[10]=(action.Target+1)/11f; result[11]=(action.Choice+1)/10f;
        if(action.Kind is Move.Buy or Move.BuyItem && observation.Shop[action.Source] is { } offer)
        {
            result[12]=(float)offer.Price/100;
            if(offer.Creature is { } unit) { result[13]=_ids[unit.SpeciesId]; result[14]=unit.Level/4f; result[15]=unit.Shiny?1:0; }
            else result[16]=_items.GetValueOrDefault(offer.ItemId!);
        }
        if(action.Kind==Move.Choose && action.Choice>=0) result[17]=_trinkets.GetValueOrDefault(observation.Choices[action.Choice].DefinitionId??"");
        if(action.Kind==Move.Choose && action.Choice>=0)
        {
            var choice=observation.Choices[action.Choice];
            result[18]=_trainers.GetValueOrDefault(choice.DefinitionId??"");
            if(choice.Effects.FirstOrDefault() is { } effect) {result[19]=(int)effect.Operation;result[20]=(int)effect.Stat;result[21]=(float)effect.Amount;result[22]=(int)effect.Target;}
            if(choice.Effects.FirstOrDefault(x=>x.Operation==Operation.GrantUnit) is { } grant)
            {result[23]=grant.DefinitionId is null?0:_ids.GetValueOrDefault(grant.DefinitionId);result[24]=(grant.MinimumLevel??1)/4f;result[25]=grant.ForceShiny?1:0;result[26]=(float)grant.Amount;result[27]=(int)grant.Stat;}
            result[28]=choice.AtShopStart?1:0;result[29]=choice.TargetRule is null?0:1;
        }
        if(action.Kind==Move.Choose && action.Choice>=0){var choice=observation.Choices[action.Choice];result[30]=choice.Effects.Select((e,i)=>(float)e.Amount*(i+1)).Sum();
            result[31]=choice.Effects.Select((e,i)=>((int)e.Operation*32+(int)e.Target)*(i+1)/2048f).Sum();
            result[32]=Symbol(choice.DefinitionId);result[33]=choice.TargetRule?.NonShiny==true?1:0;result[34]=choice.Effects.Length;}
        if(action.Source is >=0 and <10 && observation.Units[action.Source] is { } source)result[35]=_ids[source.SpeciesId];
        if(action.Target is >=0 and <10 && observation.Units[action.Target] is { } target)result[36]=_ids[target.SpeciesId];
        var deployed=0;var bench=0;
        for(var i=0;i<6;i++)if(observation.Units[i] is not null)deployed++;
        for(var i=6;i<10;i++)if(observation.Units[i] is not null)bench++;
        var afterDeployed=deployed;var afterBench=bench;
        switch(action.Kind)
        {
            case Move.Buy when action.Target is >=0 and <6: afterDeployed++;break;
            case Move.Buy when action.Target is >=6 and <10: afterBench++;break;
            case Move.Sell when action.Source is >=0 and <6: afterDeployed--;break;
            case Move.Sell when action.Source is >=6 and <10: afterBench--;break;
            case Move.Move when action.Source==10 && action.Target is >=0 and <6: afterDeployed++;break;
            case Move.Move when action.Source==10 && action.Target is >=6 and <10: afterBench++;break;
            case Move.Move when action.Source is >=0 and <6 && action.Target is >=6 and <10 && observation.Units[action.Target] is null:
                afterDeployed--;afterBench++;break;
            case Move.Move when action.Source is >=6 and <10 && action.Target is >=0 and <6 && observation.Units[action.Target] is null:
                afterBench--;afterDeployed++;break;
            case Move.Merge when action.Source is >=0 and <6: afterDeployed--;break;
            case Move.Merge when action.Source is >=6 and <10: afterBench--;break;
        }
        var gap=Math.Min(6-deployed,bench);var afterGap=Math.Min(6-afterDeployed,afterBench);
        if(action.Kind!=Move.Choose){
            var sourceSlot=action.Source;var targetSlot=action.Target;
            if(sourceSlot is >=0 and <6 && targetSlot is >=0 and <6){
                result[30]=(sourceSlot%3-targetSlot%3)/2f;result[31]=sourceSlot/3-targetSlot/3;
                result[32]=Math.Abs(sourceSlot%3-targetSlot%3)+Math.Abs(sourceSlot/3-targetSlot/3)==1?1:0;
            }
            result[33]=sourceSlot is >=6 and <10?1:action.Kind is Move.Buy or Move.BuyItem?2:0;
        }
        result[37]=afterDeployed-deployed;
        result[38]=afterGap-gap;
        result[39]=action.Kind==Move.Finish && gap>0?1:0;
    }
    private List<float> Tokens(Observation o,int decisions,int limit,ShopTurnMemory? memory)
    {
        var rows=new List<float>();
        void Row(int kind,int owner,float id,float value=0,float extra=0,float extra2=0){var row=new float[TokenWidth];row[0]=kind;row[1]=owner;row[2]=id;row[3]=value;row[4]=extra;row[5]=extra2;rows.AddRange(row);}
        void Effects(IEnumerable<Effect> effects,int owner,int depth=0){foreach(var effect in effects){var row=(float[])(_effects.TryGetValue(effect,out var cached)?cached:Descriptor(effect)).Clone();row[1]=owner;row[2]=depth;rows.AddRange(row);}}
        void Counters(Dictionary<string,double>? counters,int owner,int depth=0){if(counters is not null)foreach(var (key,value) in counters.OrderBy(x=>x.Key,StringComparer.Ordinal)){
            var row=new float[TokenWidth];row[0]=3;row[1]=owner;row[3]=Number(value);row[4]=depth;
            ulong hash=14695981039346656037;foreach(var c in key){hash^=c;hash*=1099511628211;}
            row[2]=(hash&65535)/65535f;row[5]=((hash>>16)&65535)/65535f;row[6]=((hash>>32)&65535)/65535f;row[7]=((hash>>48)&65535)/65535f;rows.AddRange(row);
        }}
        void UnitTokens(Unit? unit,int owner,int depth=0){if(unit is null)return;var species=_species[unit.SpeciesId];
            var definition=LevelRules.Resolve(species,unit.Level,unit.Shiny);var unitRow=new float[TokenWidth];
            unitRow[0]=1;unitRow[1]=owner;unitRow[2]=_ids[unit.SpeciesId];unitRow[3]=Number(unit.StoredDiscount);
            unitRow[4]=unit.Level/4f;unitRow[5]=unit.Shiny?1:0;unitRow[6]=unit.Copies/3f;unitRow[7]=depth;
            unitRow[8]=(float)(definition.Cooldown??0)/10;unitRow[9]=species.Rarity/5f;
            for(var stat=0;stat<9;stat++)unitRow[10+stat]=Number(definition.Stats[stat]+unit.Bonuses[stat]);
            unitRow[19]=Number(unit.Instance);rows.AddRange(unitRow);Effects(definition.Effects,owner,depth);
            foreach(var type in species.Types.Concat(unit.AddedTypes))Row(2,owner,_types.GetValueOrDefault(type)+1,0,depth);
            Counters(unit.Counters,owner,depth);if(unit.Devoured is not null){Row(12,owner,0,depth+1);UnitTokens(unit.Devoured,owner,depth+1);}}
        for(var i=0;i<10;i++)UnitTokens(o.Units[i],i);
        for(var i=0;i<5;i++){var offer=o.Shop[i];UnitTokens(offer?.Creature,i+10);if(offer?.ItemId is { } item){Row(4,i+10,_items[item]);Effects(rules.Items[_items[item]-1].Effects,i+10);}}
        for(var i=0;i<o.Bag.Length;i++){var entry=o.Bag[i];Row(5,16+i,_trinkets.TryGetValue(entry.Id,out var tid)?tid:Symbol(entry.Id),entry.Quantity,i);if(tid>0)Effects(rules.Trinkets[tid-1].Effects,16+i);}
        var owner=16+o.Bag.Length;
        foreach(var trainer in o.ExtraTrainers.Prepend(o.Trainer)){Row(7,owner,_trainers[trainer]);Effects(rules.Trainers[_trainers[trainer]-1].Effects,owner++);}
        for(var i=0;i<o.Choices.Length;i++){var c=o.Choices[i];Row(8,owner+i,Symbol(c.DefinitionId),i,c.AtShopStart?1:0);Effects(c.Effects,owner+i);
            if(c.TargetRule is {} target)Row(9,owner+i,Symbol(target.ExcludedType),target.MinimumLevel??0,target.MaximumLevel??0,target.NonShiny?1:0);}
        owner+=o.Choices.Length;UnitTokens(o.Overflow,15);Counters(o.Counters,-1);
        Row(10,-1,0,(limit-decisions)/(float)limit,(int)(memory?.LastAction??Move.Finish)/8f,(memory?.RepeatedKind??0)/64f);
        if(o.VisibleState is {} v){Row(11,-1,(int)v.ActiveChoicePhase,v.CarryShop?1:0,v.SecondChanceUsed?1:0,v.PendingShopStart?1:0);
            Row(11,-1,Symbol(v.CurrentEvent),v.TakeAllChoices?1:0);foreach(var id in v.ForeignPool)Row(13,-1,_ids.GetValueOrDefault(id));
            foreach(var u in v.PendingUnits)UnitTokens(u,owner++);Effects(v.PendingShopEffects,-1);
            foreach(var buff in v.PendingPurchaseBuffs){var slot=Array.FindIndex(o.Units,x=>x?.Instance==buff.SourceInstance);Row(14,slot,0,Number(buff.SourceInstance));Effects([buff.Effect],slot);}}
        return rows;
    }
}
