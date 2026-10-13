namespace BatomonHelper.Simulation;

/// <summary>Current-build item effects. Match the source text too, so future edits cannot silently reuse stale behavior.</summary>
public static class ItemRules
{
    private static Effect E(Operation operation, Target target = Target.All, Stat stat = Stat.Damage,
        double amount = 0, string? type = null, int? rarity = null, int? min = null, int? max = null,
        int? maxRarity = null, int count = 1, bool noAbility = false, string? id = null) =>
        new(Trigger.Bought, operation, target, stat, amount, true, type, Rarity: rarity,
            MinimumLevel: min, MaximumLevel: max, DefinitionId: id, MaximumRarity: maxRarity,
            TargetCount: count, RequireNoAbility: noAbility);

    public static (string Text, Effect[] Effects)? Definition(string id) => id switch
    {
        "apex-bait" => ("Rank up the shop.", [E(Operation.ShopRank, amount: 1)]),
        "basic-bait" => ("Lower the shop rank.", [E(Operation.ShopRank, amount: -1)]),
        "basic-candy" => ("Level up a random level 1 Common or Uncommon monster.", [E(Operation.LevelUp, Target.RandomAlly, min: 1, max: 1, maxRarity: 1)]),
        "battery-pack" => ("Give your Electric monsters +1 Shock.", [E(Operation.Add, stat: Stat.Shock, amount: 1, type: "electric")]),
        "black-feather" => ("Give 1 random monsters +1 Multicast.", [E(Operation.Add, Target.RandomAlly, Stat.Multicast, 1)]),
        "black-sludge" => ("Give your Toxic monsters +1 Poison.", [E(Operation.Add, stat: Stat.Poison, amount: 1, type: "toxic")]),
        "blue-ticket" => ("Reroll a shop with all Rare monsters.", [E(Operation.RollShop, rarity: 2)]),
        "cake" => ("Give 2 random monsters +5 Damage.", [E(Operation.Add, Target.RandomAlly, amount: 5, count: 2)]),
        "coffee" => ("At the start of the next battle, Batomon On Battle Start abilities activate an additional time.", [E(Operation.NextBattleStartRepeat, amount: 1)]),
        "coupon" => ("Reduce the cost of monsters in your shop by 3.", [E(Operation.DiscountShop, amount: 3)]),
        "crimson-gift" => ("Gain a random Mythical Trinket.", [E(Operation.GrantTrinket, rarity: 5)]),
        "crimson-ticket" => ("Reroll a shop with all Mythical monsters.", [E(Operation.RollShop, rarity: 5)]),
        "dowsing-rod" => ("Choose a Common Trinket gift.", [E(Operation.TrinketGift, rarity: 0)]),
        "fake-coin" => ("Gain 1 free reroll.", [E(Operation.FreeReroll, amount: 1)]),
        "feast" => ("Give your monsters +5 Damage.", [E(Operation.Add, amount: 5)]),
        "focus-pill" => ("Your monsters with no abilities gain +15% Cooldown Speed.", [E(Operation.Add, stat: Stat.Speed, amount: .15, noAbility: true)]),
        "gold-powder" => ("Give a random monster +20 Sell Value.", [E(Operation.Add, Target.RandomAlly, Stat.SellValue, 20)]),
        "golden-gift" => ("Gain a random Legendary Trinket.", [E(Operation.GrantTrinket, rarity: 4)]),
        "golden-ticket" => ("Reroll a shop with all Legendary monsters.", [E(Operation.RollShop, rarity: 4)]),
        "gray-chip" => ("If you win the next battle, gain $15.", [E(Operation.NextVictoryGold, amount: 15)]),
        "gray-ticket" => ("Reroll a shop with all Common monsters.", [E(Operation.RollShop, rarity: 0)]),
        "green-stone" => ("Transform a random Common monster into an Uncommon monster.", [E(Operation.Transform, Target.RandomAlly, rarity: 0, id: "rarity:1")]),
        "green-ticket" => ("Reroll a shop with all Uncommon monsters.", [E(Operation.RollShop, rarity: 1)]),
        "hot-pepper" => ("Give your Fire monsters +1 Burn.", [E(Operation.Add, stat: Stat.Burn, amount: 1, type: "fire")]),
        "lootbox" => ("Get a random Legendary monster.", [E(Operation.GrantUnit, rarity: 4)]),
        "lucky-coin" => ("Gain $3. You can use 1 more item(s) today.", [E(Operation.Gold, amount: 3), E(Operation.ItemUses, amount: 1)]),
        "magic-lasso" => ("Gain an exact copy of the enemy's top middle Batomon in the next battle.", [E(Operation.NextCopyEnemy, amount: 1)]),
        "mystic-pearl" => ("Give your Water monsters +10 Heal.", [E(Operation.Add, stat: Stat.Heal, amount: 10, type: "water")]),
        "nana-berry" => ("Give the bottom right monster +5% Cooldown Speed. You can use 1 more item(s) today.", [E(Operation.Add, Target.BottomRight, Stat.Speed, .05), E(Operation.ItemUses, amount: 1)]),
        "pom-berry" => ("Give the bottom right monster +8 Damage. You can use 1 more item(s) today.", [E(Operation.Add, Target.BottomRight, amount: 8), E(Operation.ItemUses, amount: 1)]),
        "purple-gift" => ("Gain a random Super Rare Trinket.", [E(Operation.GrantTrinket, rarity: 3)]),
        "purple-ticket" => ("Reroll a shop with all Super Rare monsters.", [E(Operation.RollShop, rarity: 3)]),
        "rare-candy" => ("Level up a random level 1 monster.", [E(Operation.LevelUp, Target.RandomAlly, min: 1, max: 1)]),
        "recruiting-flyer" => ("Get a random level 3 Uncommon monster.", [E(Operation.GrantUnit, rarity: 1, min: 3)]),
        "red-coin" => ("+$20 and lose 1 life.", [E(Operation.Gold, amount: 20), E(Operation.LoseLife, amount: 1)]),
        "shiny-berry" => ("Turn a random monster SHINY. You can use 1 more item(s) today.", [E(Operation.Shiny, Target.RandomAlly), E(Operation.ItemUses, amount: 1)]),
        "shiny-pebble" => ("Give your Rock monsters +20 Shield.", [E(Operation.Add, stat: Stat.Shield, amount: 20, type: "rock")]),
        "tote-bag" => ("You can use 2 more item(s) today.", [E(Operation.ItemUses, amount: 2)]),
        "ultra-candy" => ("Level up a random level 3 monster.", [E(Operation.LevelUp, Target.RandomAlly, min: 3, max: 3)]),
        "voucher" => ("Buy a random monster from the shop for free.", [E(Operation.RecruitOffer)]),
        _ => null
    };

    public static bool TryCompile(string id, string? text, out Effect[] effects)
    {
        var definition = Definition(id);
        effects = definition?.Effects ?? [];
        return definition is { } d && text?.Trim() == d.Text;
    }
}
