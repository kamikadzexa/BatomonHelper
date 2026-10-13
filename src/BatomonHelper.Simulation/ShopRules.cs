namespace BatomonHelper.Simulation;

/// <summary>Shop-specific effects keyed by both identity and pinned current text.</summary>
public static class ShopRules
{
    private static Effect Trait(ShopTrait trait,double amount=1,string? type=null,int minimumDay=1) =>
        new(Trigger.Ongoing,Operation.ShopModifier,Target.All,Amount:amount,Type:type,ShopTrait:trait,MinimumDay:minimumDay);
    private static Effect Spawn(Stat stat,double amount,string? type=null,bool percent=false) =>
        new(Trigger.ShopSpawn,percent && stat!=Stat.Speed?Operation.Multiply:Operation.Add,Target.All,stat,amount,true,type);
    public static (string Text,Effect[] Effects)? Definition(string category,string id) => (category,id) switch
    {
        ("trinkets","research-notes") => ("Legendary monsters are much more likely to appear in your shop.",[Trait(ShopTrait.LegendaryWeight,20)]),
        ("trinkets","vip-pass") => ("Your shop no longer stocks anything Common or Uncommon.",[Trait(ShopTrait.MinimumRarity,2)]),
        ("trinkets","rainbow-pearl") => ("All monsters in your shop are SHINY.",[Trait(ShopTrait.ForcedShiny)]),
        ("trinkets","rocket-boots") => ("Monsters in your shop gain Flying typing.",[new(Trigger.ShopSpawn,Operation.AddType,Target.All,Permanent:true,Type:"flying")]),
        ("trinkets","quick-bell") => ("Monsters in your shop have +4% Cooldown Speed.",[Spawn(Stat.Speed,.04)]),
        ("trinkets","blitz-bell") => ("Monsters in your shop have +25% Cooldown Speed.",[Spawn(Stat.Speed,.25)]),
        ("trinkets","power-bell") => ("Monsters in your shop have +25 Damage.",[Spawn(Stat.Damage,25)]),
        ("trinkets","mighty-bell") => ("Monsters in your shop have +300 Damage.",[Spawn(Stat.Damage,300)]),
        ("trinkets","fire-bell") => ("Fire monsters in your shop have +5 Burn.",[Spawn(Stat.Burn,5,"fire")]),
        ("trinkets","poison-bell") => ("Toxic monsters in your shop have +6 Poison.",[Spawn(Stat.Poison,6,"toxic")]),
        ("trinkets","sapphire-ring") => ("Monsters in your shop have +10% Heal.",[Spawn(Stat.Heal,.1,percent:true)]),
        ("trinkets","topaz-ring") => ("Monsters in your shop have +10% Shield.",[Spawn(Stat.Shield,.1,percent:true)]),
        ("trinkets","bug-net") => ("Bug monsters in the shop cost 3 less.",[Trait(ShopTrait.CreatureDiscount,3,"bug")]),
        ("trinkets","bargain-bin") => ("The first Common monster you buy each day is free.",[Trait(ShopTrait.FirstCommonFree)]),
        ("trinkets","echo-bell") => ("When you buy a 0 cost item, use it an additional time.",[Trait(ShopTrait.RepeatZeroItem)]),
        ("trinkets","echo-charm") => ("Your monsters' On Bought abilities activate an additional time.",[Trait(ShopTrait.RepeatBought)]),
        ("trinkets","membership-card") => ("You can use 1 additional Item each day.",[Trait(ShopTrait.DailyItemUses)]),
        ("trinkets","gold-nugget") => ("Increase Income by $3.",[Trait(ShopTrait.Income,3)]),
        ("trinkets","gold-bar") => ("Increase Income by $10.",[Trait(ShopTrait.Income,10)]),
        ("trinkets","gold-o-matic") => ("Increase Income by $30.",[Trait(ShopTrait.Income,30)]),
        ("trinkets","fools-gold") => ("Increase Income by $15. The shop no longer stocks Item.",[Trait(ShopTrait.Income,15),Trait(ShopTrait.NoItems)]),
        ("trinkets","market-license") => ("Increase shop rank by 1.",[new(Trigger.RunStart,Operation.ShopRank,Target.All,Amount:1)]),
        ("trinkets","arcade-coins") => ("Gain 10 free rerolls.",[new(Trigger.RunStart,Operation.FreeReroll,Target.All,Amount:10)]),
        ("trinkets","power-pouch") => ("When you buy an item, give your monsters +2 Damage.",[new(Trigger.ItemBought,Operation.Add,Target.All,Amount:2,Permanent:true)]),
        ("trinkets","silver-watch") => ("When a monster levels up from a merge, it gains +12% Cooldown Speed.",[new(Trigger.AllyLevelUp,Operation.Add,Target.Self,Stat.Speed,.12,true)]),
        ("trainers","bug-catcher") => ("The first Bug monster you buy each day is free.",[Trait(ShopTrait.FirstBugFree)]),
        ("trainers","youngster-f") => ("SHINY monsters are more likely to appear.",[Trait(ShopTrait.ShinyMultiplier,20)]),
        ("trainers","youngster-m") => ("Gain 3 free rerolls every day.",[new(Trigger.DayStart,Operation.FreeReroll,Target.All,Amount:3)]),
        ("trainers","lady") => ("Gain shop rank +2 and $10.",[new(Trigger.RunStart,Operation.ShopRank,Target.All,Amount:2),new(Trigger.RunStart,Operation.Gold,Target.All,Amount:10)]),
        ("trainers","shopkeeper") => ("Your shop stocks Item 1 rarity tier higher and cost 15% less.",[Trait(ShopTrait.ItemTierBonus),Trait(ShopTrait.ItemDiscount,.15)]),
        ("trainers","gentleman") => ("From day 4 onwards, your shop no longer stocks Common or Uncommon monsters.",[Trait(ShopTrait.MinimumRarity,2,minimumDay:4)]),
        _ => null
    };
    public static bool TryCompile(string category,string id,string? text,out Effect[] effects)
    {
        var definition=Definition(category,id);effects=definition?.Effects??[];
        return definition is { } d && d.Text==text?.Trim();
    }
}
