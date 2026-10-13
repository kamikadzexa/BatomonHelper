using BatomonHelper.Core;
namespace BatomonHelper.Simulation;

public static class EncounterRules
{
    private static Effect Unit(string id,int level=1,bool shiny=false,Stat stat=Stat.Damage,double bonus=0) =>
        new Effect(Trigger.Bought,Operation.GrantUnit,Target.All,stat,bonus,DefinitionId:id,MinimumLevel:level,ForceShiny:shiny);
    private static Effect Trinket(string id) => new Effect(Trigger.Bought,Operation.GrantTrinket,Target.All,DefinitionId:id);
    private static Effect Gold(double amount) => new Effect(Trigger.Bought,Operation.Gold,Target.All,Amount:amount);
    private static Choice C(string label,params Effect[] effects) => new(label,null,effects);
    public static EncounterDefinition? Definition(string id)
    {
        var url="https://batomon.com/"+id;
        return id switch
        {
            "events/baby-basilisk" => new(id,"Baby Basilisk",2,[C("Adopt it",Unit("ignit")),C("Help it find its parents",new Effect(Trigger.Bought,Operation.TrinketGift,Target.All,Rarity:2)),C("Claim the missing Batomon bounty",Gold(40))],url),
            "events/bug-collector" => new(id,"Bug Collector",1,[C("Take the fighter ant",Unit("guardiant",2)),C("Take the gold beetle",Unit("gildshell",bonus:20)),C("Take the boxing mantis",Unit("brawlmantis",stat:Stat.Multicast,bonus:1))],url),
            "events/dragon-tamer" => new(id,"Dragon Tamer",3,[C("Adopt the one with the big horn",Unit("saberhorn",shiny:true)),C("Adopt the one with the crown",Unit("dragonarch",shiny:true)),C("Adopt the long one",Unit("onsetra",shiny:true))],url),
            "events/dragons-nest" => new(id,"Dragons Nest",1,[C("Take the egg",Unit("dragon-egg-0")),C("Take the sword",Trinket("heros-sword")),C("Take the coins",Gold(20))],url),
            "events/dryad-statue" or "events/dryad-statue-oshima" => new(id,id.EndsWith("oshima")?"Dryad Statue Oshima":"Dryad Statue",3,[
                new("Pray to it",null,[new Effect(Trigger.Bought,Operation.AddType,Target.Selected,Permanent:true,Type:"grass"),Unit(id.EndsWith("oshima")?"ginsage":"dryadell")],new(ExcludedType:"grass")),
                C("Take the gem",Trinket("mysterious-gem")),C("Gently touch it",Trinket("dryads-charm"))],url,Region:id.EndsWith("oshima")?"Oshima":"Pantra"),
            "events/fossil-discovery" => new(id,"Fossil Discovery",2,[C("Take it to a lab and revive it",Unit("talonite")),C("Keep it",Trinket("winged-fossil")),C("Sell it",Gold(40))],url,3),
            "events/genius-inventor" => new(id,"Genius Inventor",3,[
                new("Test the Recombobulator",null,[new Effect(Trigger.Bought,Operation.Recombine,Target.All,Amount:1,Permanent:true)],AtShopStart:true),
                new("Test the Shockify Ray",null,[new Effect(Trigger.Bought,Operation.AddType,Target.Selected,Permanent:true,Type:"electric"),new Effect(Trigger.Bought,Operation.Add,Target.Selected,Stat.Shock,5,true)],new(ExcludedType:"electric")),
                C("Test the Gold-o-matic",Trinket("gold-o-matic"))],url,5),
            "events/meteor-crash" => new(id,"Meteor Crash",2,[new("Touch the glowing gem",null,[new Effect(Trigger.Bought,Operation.LevelUp,Target.Selected,Permanent:true)],new(MaximumLevel:1)),C("Mine the meteor",Trinket("meteorite")),C("Take some meteor debris and sell it",Gold(40))],url),
            "events/pond-fairy" => new(id,"Pond Fairy",2,[C("Wish for fame",new Effect(Trigger.Bought,Operation.ShopRank,Target.All,Amount:2)),C("Wish for wealth",Trinket("gold-bar")),C("Capture the fairy",Unit("wishwash"))],url),
            "events/professor-encounter" => new(id,"Professor Encounter",3,[new("Get advice on monster training",null,[new Effect(Trigger.Bought,Operation.LevelUp,Target.Selected,Permanent:true)],new(MaximumLevel:3)),C("Ask about his research",Trinket("research-notes")),C("Get investment advice",Gold(60))],url),
            "events/rainbow-berry" => new(id,"Rainbow Berry",1,[new("Feed it to a monster",null,[new Effect(Trigger.Bought,Operation.Shiny,Target.Selected,Permanent:true)],new(NonShiny:true)),C("Take it",Trinket("rainbow-berry")),C("Sell it at the market",Gold(20))],url),
            "events/second-chance" => new(id,"Second Chance",4,[C("Accept a life and a trinket",new Effect(Trigger.Bought,Operation.TrinketGift,Target.All,Rarity:3)),C("Accept a life and a monster",new Effect(Trigger.Bought,Operation.GrantUnit,Target.All,Rarity:3,MinimumLevel:2)),C("Accept a life and a charm",Trinket("mysterious-charm"))],url),
            "events/traveling-merchant" => new(id,"Traveling Merchant",1,[C("Take the exotic monster",new Effect(Trigger.Bought,Operation.GrantUnit,Target.All,Rarity:2,Foreign:true,AdmitToShop:true)),C("Take the strange lens",Trinket("kaleidoscope")),C("Take the dull-looking gem",Trinket("fake-diamond"))],url,6),
            _=>null
        };
    }
    public static EncounterDefinition[] Compile(StrategyKnowledge knowledge,List<RuleIssue> issues) => knowledge.References
        .Where(x=>x.Id.StartsWith("events/") && x.Id!="events/endless-boss-prep").Select(reference=>
        {
            var definition=Definition(reference.Id);
            if(definition is null || definition.Choices.Any(x=>!reference.Text.Contains(x.Label,StringComparison.Ordinal)))
            {issues.Add(new(reference.Id,"Event","Unknown or changed encounter choices require a handler update.",reference.SourceUrl));return null;}
            return definition;
        }).OfType<EncounterDefinition>().ToArray();
}
