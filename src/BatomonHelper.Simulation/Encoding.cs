using System.Text.Json;
using System.Text.Json.Serialization;
namespace BatomonHelper.Simulation;

/// <summary>Versioned, player-visible numeric contract. Random state and future opponents are excluded.</summary>
public sealed class PolicyEncoding(RulesManifest rules)
{
    public const int Version=4;
    public const int TokenWidth=12;
    public const int ActionWidth=40;
    private readonly Dictionary<string,int> _ids=rules.Species.Select((x,i)=>(x.Id,i)).ToDictionary(x=>x.Id,x=>x.i+1);
    private readonly Dictionary<string,int> _types=rules.Species.SelectMany(x=>x.Types).Distinct().Order().Select((x,i)=>(x,i)).ToDictionary(x=>x.x,x=>x.i);
    public int ObservationWidth => 16+15*18+rules.Trinkets.Length+rules.Trainers.Length;
    public float[] Observation(Observation observation)
    {
        var result=new float[ObservationWidth]; var n=0;
        foreach(var value in new double[] {observation.Day/20d,observation.Lives/10d,observation.Wins/10d,observation.Gold/100d,
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
                var species=rules.Species.First(x=>x.Id==unit.SpeciesId); var level=LevelRules.Resolve(species,unit.Level,unit.Shiny);
                result[n++]=(float)(level.Cooldown??0)/10;
                for(var i=0;i<9;i++) result[n++]=(float)(level.Stats[i]+unit.Bonuses[i])/(i>=6?10:100);
                result[n++]=species.Rarity/5f;
                result[n++]=unit.AddedTypes.Where(_types.ContainsKey).Sum(x=>1<<_types[x]);
            }
            if(slot>=10 && observation.Shop[slot-10] is { } offer)
            {
                result[start+16]=offer.ItemId is null?0:Array.FindIndex(rules.Items,x=>x.Id==offer.ItemId)+1;
                result[start+17]=(float)offer.Price/100;
            }
            n=start+18;
        }
        foreach(var trinket in rules.Trinkets) result[n++]=observation.Bag.Where(x=>x.Id==trinket.Id).Sum(x=>x.Quantity);
        foreach(var trainer in rules.Trainers) result[n++]=observation.Trainer==trainer.Id || observation.ExtraTrainers.Contains(trainer.Id)?1:0;
        var tokens=Tokens(new { Units=observation.Units.Concat(observation.Shop.Select(x=>x?.Creature)).Select(x=>x is null?null:new {x.Instance,x.Counters,x.Devoured,x.AddedTypes,x.StoredDiscount}).ToArray(),
            observation.Bag,observation.ExtraTrainers,observation.Counters,observation.Choices,observation.Overflow,observation.VisibleState });
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
            else result[16]=Array.FindIndex(rules.Items,x=>x.Id==offer.ItemId)+1;
        }
        if(action.Kind==Move.Choose && action.Choice>=0) result[17]=Array.FindIndex(rules.Trinkets,x=>x.Id==observation.Choices[action.Choice].DefinitionId)+1;
        if(action.Kind==Move.Choose && action.Choice>=0)
        {
            var choice=observation.Choices[action.Choice];
            result[18]=Array.FindIndex(rules.Trainers,x=>x.Id==choice.DefinitionId)+1;
            if(choice.Effects.FirstOrDefault() is { } effect) {result[19]=(int)effect.Operation;result[20]=(int)effect.Stat;result[21]=(float)effect.Amount;result[22]=(int)effect.Target;}
            if(choice.Effects.FirstOrDefault(x=>x.Operation==Operation.GrantUnit) is { } grant)
            {result[23]=grant.DefinitionId is null?0:_ids.GetValueOrDefault(grant.DefinitionId);result[24]=(grant.MinimumLevel??1)/4f;result[25]=grant.ForceShiny?1:0;result[26]=(float)grant.Amount;result[27]=(int)grant.Stat;}
            result[28]=choice.AtShopStart?1:0;result[29]=choice.TargetRule is null?0:1;
        }
        if(action.Kind==Move.Choose && action.Choice>=0){var choice=observation.Choices[action.Choice];Fingerprint(JsonSerializer.Serialize(choice),result,30);result[34]=choice.Effects.Length;}
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
        result[37]=afterDeployed-deployed;
        result[38]=afterGap-gap;
        result[39]=action.Kind==Move.Finish && gap>0?1:0;
    }
    private static readonly JsonSerializerOptions TokenJson=new(){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingDefault};
    private static ulong Hash(string text){ulong hash=14695981039346656037;foreach(var c in text){hash^=c;hash*=1099511628211;}return hash;}
    private static void Fingerprint(string text,Span<float> target,int offset){var hash=Hash(text);for(var i=0;i<4;i++)target[offset+i]=((hash>>(16*i))&65535)/65535f;}
    private List<float> Tokens(object value)
    {
        using var document=JsonSerializer.SerializeToDocument(value,TokenJson);var result=new List<float>();
        void Walk(JsonElement node,string path,int depth){
            if(node.ValueKind==JsonValueKind.Object){foreach(var property in node.EnumerateObject())Walk(property.Value,path+"/"+property.Name,depth+1);return;}
            if(node.ValueKind==JsonValueKind.Array){var index=0;foreach(var item in node.EnumerateArray())Walk(item,path+"/"+index++,depth+1);return;}
            var token=new float[TokenWidth];token[0]=(int)node.ValueKind/8f;Fingerprint(path,token,1);
            if(node.ValueKind==JsonValueKind.String){var text=node.GetString()!;Fingerprint(text,token,5);token[9]=_ids.TryGetValue(text,out var species)?species:0;}
            else if(node.ValueKind==JsonValueKind.Number){var number=node.GetDouble();token[9]=(float)(Math.Sign(number)*Math.Log(1+Math.Abs(number))/10);}
            else if(node.ValueKind==JsonValueKind.True)token[9]=1;
            token[10]=depth/16f;token[11]=result.Count/(float)TokenWidth/1000;result.AddRange(token);
        }
        Walk(document.RootElement,"",0);return result;
    }
}
