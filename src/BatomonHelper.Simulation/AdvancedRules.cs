namespace BatomonHelper.Simulation;

/// <summary>Explicit current-build definitions with identity-specific executable handlers.</summary>
public static class AdvancedRules
{
    public static (string Text,Effect[] Effects)? Definition(string category,string id) => (category,id) switch
    {
        ("trinkets","alphas-crown") => ("On Battle Start Your monsters' Damage is set to your top-middle monster's Damage for this battle.", [new(Trigger.BattleStart,Operation.InventoryRule,Target.All,DefinitionId:"alphas-crown")]),
        ("trinkets","ancient-plume") => ("On Battle Start If you only have 1 monster, give it +1 Multicast permanently and destroy this.", [new(Trigger.BattleStart,Operation.InventoryRule,Target.All,DefinitionId:"ancient-plume")]),
        ("trinkets","blue-incense") => ("The next 5 shops will contain only Rare monsters.", [new(Trigger.RunStart,Operation.InventoryRule,Target.All,DefinitionId:"blue-incense")]),
        ("trinkets","candy-jar") => ("On Battle Start Your Common and Uncommon monsters permanently level up to level 3.", [new(Trigger.BattleStart,Operation.InventoryRule,Target.All,DefinitionId:"candy-jar")]),
        ("trinkets","fake-diamond") => ("The next time you lose a battle, gain $40.", [new(Trigger.Defeat,Operation.InventoryRule,Target.All,DefinitionId:"fake-diamond")]),
        ("trinkets","giant-club") => ("Your monsters with a Cooldown of 5 seconds or above have +70% stats (except Cooldown and Multicast).", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"giant-club")]),
        ("trinkets","gold-incense") => ("The next 5 shops will contain only Legendary monsters.", [new(Trigger.RunStart,Operation.InventoryRule,Target.All,DefinitionId:"gold-incense")]),
        ("trinkets","greedy-gloves") => ("You can take all of the choices from your next Trinket gift.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"greedy-gloves")]),
        ("trinkets","grow-lamp") => ("Your Grass monsters only need 2 copies to level up.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"grow-lamp")]),
        ("trinkets","kaleidoscope") => ("Your monsters that don't share a type with any ally have +25% Cooldown Speed.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"kaleidoscope")]),
        ("trinkets","locked-box") => ("The next time you lose a battle, gain a random non-unique Trinket of the highest rarity your shop rank allows.", [new(Trigger.Defeat,Operation.InventoryRule,Target.All,DefinitionId:"locked-box")]),
        ("trinkets","mega-duplicator") => ("Gain 2 additional copies of the next non-unique Trinket from a gift.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"mega-duplicator")]),
        ("trinkets","mega-upgrade-disc") => ("When a monster reaches level 3, level it up again and destroy this.", [new(Trigger.AllyAnyLevelUp,Operation.InventoryRule,Target.All,DefinitionId:"mega-upgrade-disc")]),
        ("trinkets","metal-bat") => ("On Battle Start Your monster with the highest Cooldown gains +50% Damage for this battle.", [new(Trigger.BattleStart,Operation.InventoryRule,Target.All,DefinitionId:"metal-bat")]),
        ("trinkets","mini-duplicator") => ("Gain an additional copy of the next non-unique Trinket from a gift.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"mini-duplicator")]),
        ("trinkets","mysterious-mask") => ("Choose a trainer and gain their ability.", [new(Trigger.RunStart,Operation.InventoryRule,Target.All,DefinitionId:"mysterious-mask")]),
        ("trinkets","mystic-incense") => ("A Mythical monster will appear in your next shop.", [new(Trigger.RunStart,Operation.InventoryRule,Target.All,DefinitionId:"mystic-incense")]),
        ("trinkets","piggy-bank") => ("Gain +$1 for each $10 you have at the end of each day.", [new(Trigger.DayEnd,Operation.InventoryRule,Target.All,DefinitionId:"piggy-bank")]),
        ("trinkets","power-band") => ("Give the first monster you buy each day +20 Damage.", [new(Trigger.Bought,Operation.InventoryRule,Target.All,DefinitionId:"power-band")]),
        ("trinkets","purple-incense") => ("The next 5 shops will contain only Super Rare monsters.", [new(Trigger.RunStart,Operation.InventoryRule,Target.All,DefinitionId:"purple-incense")]),
        ("trinkets","rigged-dice") => ("The first shop each day stocks only the rarest monsters and items your shop rank allows.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"rigged-dice")]),
        ("trinkets","sapphire-amulet") => ("You receive 25% more Heal.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"sapphire-amulet")]),
        ("trinkets","small-club") => ("Your monsters with a Cooldown of 5 seconds or above have +20% Damage.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"small-club")]),
        ("trinkets","speed-whistle") => ("The next monster you buy has +12% Cooldown Speed.", [new(Trigger.Bought,Operation.InventoryRule,Target.All,DefinitionId:"speed-whistle")]),
        ("trinkets","topaz-amulet") => ("You receive 25% more Shield.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"topaz-amulet")]),
        ("trinkets","treasure-map") => ("Your next Trinket gift choices are 1 rarity tier higher.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"treasure-map")]),
        ("trinkets","ultra-duplicator") => ("Gain an additional copy of each non-unique Trinket from a gift.", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"ultra-duplicator")]),
        ("trinkets","upgrade-disc") => ("When you buy a monster, level it up and destroy this.", [new(Trigger.Bought,Operation.InventoryRule,Target.All,DefinitionId:"upgrade-disc")]),
        ("trinkets","zenith-stone") => ("When your monsters gain stats, they gain +80% more (except Multicast and % bonuses).", [new(Trigger.Ongoing,Operation.InventoryRule,Target.All,DefinitionId:"zenith-stone")]),
        ("trainers","black-belt") => ("On Battle Start If you have exactly 1 Fighting monster on your team, activate its On Victory ability.", [new(Trigger.BattleStart,Operation.TrainerRule,Target.All,DefinitionId:"black-belt")]),
        ("trainers","burglar") => ("Trinket gifts only offer 2 choices, but you can take both of them.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"burglar")]),
        ("trainers","pyromaniac") => ("Your singled-typed monsters gain Fire typing. Your Fire monsters have +2 Burn.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"pyromaniac")]),
        ("trainers","chemist") => ("Your Toxic monsters have +3 Poison. When any monster levels up, increase this effect by +1 Poison.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"chemist")]),
        ("trainers","egg-breeder") => ("Gain a Purple Egg that hatches into a level 2 Super Rare monster in 5 days.", [new(Trigger.RunStart,Operation.TrainerRule,Target.All,DefinitionId:"egg-breeder")]),
        ("trainers","gamer") => ("Gain a Mythical monster and $30 on day 9.", [new(Trigger.DayStart,Operation.TrainerRule,Target.All,DefinitionId:"gamer")]),
        ("trainers","mad-scientist") => ("On day 7, transform monsters on your active team into random level 1 Legendary monsters.", [new(Trigger.DayStart,Operation.TrainerRule,Target.All,DefinitionId:"mad-scientist")]),
        ("trainers","masked-man") => ("Every 4 days, choose a trainer and gain their ability.", [new(Trigger.DayStart,Operation.TrainerRule,Target.All,DefinitionId:"masked-man")]),
        ("trainers","monster-ranger") => ("Start with an Uncommon monster. Get another copy of that monster every 2 days.", [new(Trigger.DayStart,Operation.TrainerRule,Target.All,DefinitionId:"monster-ranger")]),
        ("trainers","musician") => ("If you have exactly 1 Electric monster on your team, it will have +150% Shock.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"musician")]),
        ("trainers","painter") => ("Random species will be painted with every type this run.", [new(Trigger.RunStart,Operation.TrainerRule,Target.All,DefinitionId:"painter")]),
        ("trainers","scavenger") => ("Gain an additional copy of each non-unique Common or Uncommon Trinket from a gift.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"scavenger")]),
        ("trainers","smuggler") => ("Batomon from other regions appear in your shop and cost 25% less.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"smuggler")]),
        ("trainers","swim-coach") => ("Gain a random Water monster each day.", [new(Trigger.DayStart,Operation.TrainerRule,Target.All,DefinitionId:"swim-coach")]),
        ("trainers","treasure-hunter") => ("When you get Trinket gifts, your choices are 1 rarity tier higher.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"treasure-hunter")]),
        ("trainers","twins") => ("When monsters merge into level 3, gain an exact copy with a sell value of 0.", [new(Trigger.Ongoing,Operation.TrainerRule,Target.All,DefinitionId:"twins")]),
        _ => null
    };
    public static bool TryCompile(string category,string id,string? text,out Effect[] effects)
    {
        var definition=Definition(category,id);effects=definition?.Effects??[];
        return definition is { } d && d.Text==text?.Trim();
    }
}
