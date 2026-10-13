using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ShopSimulationTests
{
    private static RulesManifest Fixture()
    {
        var basis=RuleCompiler.Fixture();
        var species=Enumerable.Range(0,6).Select(i=>basis.Species[0] with {Id="unit-"+i,MergeGroup="unit-"+i,Rarity=i,
            Types=i<=1?["bug"]:i==2?["toxic"]:["fire"],Cost=20,Shiny=basis.Species[0].Normal}).ToArray();
        var trinketIds=new[]{"research-notes","vip-pass","rainbow-pearl","rocket-boots","quick-bell","blitz-bell","power-bell","mighty-bell",
            "fire-bell","poison-bell","sapphire-ring","topaz-ring","bug-net","bargain-bin","echo-bell","echo-charm","membership-card",
            "gold-nugget","gold-bar","gold-o-matic","fools-gold","market-license","arcade-coins","power-pouch","silver-watch"};
        var trainerIds=new[]{"bug-catcher","youngster-f","youngster-m","lady","shopkeeper","gentleman"};
        InventoryDefinition Definition(string category,string id)
        {var definition=ShopRules.Definition(category,id)!.Value;return new(id,id,0,false,0,definition.Effects,"fixture",definition.Text);}
        var coin=ItemRules.Definition("fake-coin")!.Value;
        var ticket=ItemRules.Definition("gray-ticket")!.Value;
        return basis with {Species=species,Trinkets=trinketIds.Select(x=>Definition("trinkets",x)).ToArray(),
            Trainers=basis.Trainers.Concat(trainerIds.Select(x=>Definition("trainers",x))).ToArray(),
            Items=[new("fake-coin","Coin",0,false,10,coin.Effects,"fixture",coin.Text),new("gray-ticket","Ticket",0,false,0,ticket.Effects,"fixture",ticket.Text),
                new("higher-tier","Tier item",1,false,10,[],"fixture","")]};
    }
    private static GameEnvironment Ready(string trainer="fixture")
    {var environment=new GameEnvironment(Fixture());environment.Reset(123,trainer);environment.State.Gold=1000;return environment;}
    private static void Bag(GameEnvironment environment,params BagEntry[] entries)
    {environment.State.Bag=entries;environment.GetObservation();}

    [Fact]
    public void Every_pinned_shop_definition_compiles_and_future_text_is_rejected()
    {
        var rules=Fixture();
        foreach(var definition in rules.Trinkets.Concat(rules.Trainers.Where(x=>x.Id!="fixture")))
        {
            var category=rules.Trinkets.Contains(definition)?"trinkets":"trainers";
            Assert.True(ShopRules.TryCompile(category,definition.Id,definition.Description,out var effects));Assert.NotEmpty(effects);
            Assert.False(ShopRules.TryCompile(category,definition.Id,definition.Description+" Unknown.",out _));
        }
    }

    [Fact]
    public void Research_notes_adds_weight_before_normalization_and_vip_never_rolls_mythical()
    {
        var environment=Ready();Bag(environment,new BagEntry("research-notes",2));var odds=environment.GetShopOdds();
        Assert.Equal(100d/140,odds[0],10);Assert.Equal(40d/140,odds[4],10);Assert.Equal(1,odds.Sum(),10);
        Bag(environment,new BagEntry("vip-pass",1));Assert.Equal([0d,0,1,0,0],environment.GetShopOdds());
        for(var roll=0;roll<20;roll++)
        {
            environment.Step(new(Move.Reroll));
            Assert.All(environment.State.Shop,x=>Assert.Equal("unit-2",x!.Creature!.SpeciesId));
        }
    }

    [Fact]
    public void Shop_buffs_are_applied_once_per_inventory_instance_and_survive_purchase()
    {
        var environment=Ready();Bag(environment,new BagEntry("power-bell",2),new("quick-bell",1),new("rocket-boots",1),new("rainbow-pearl",1));
        var observed=environment.GetObservation();environment.GetLegalActions();environment.GetObservation();
        var unit=observed.Shop[0]!.Creature!;Assert.Equal(50,unit.Bonuses[0]);Assert.Equal(.04,unit.Bonuses[6],8);
        Assert.True(unit.Shiny);Assert.Equal(["flying"],unit.AddedTypes);
        Assert.Equal(4,environment.State.Bag.Select(x=>x.Instance).Distinct().Count());
        var bought=environment.State.Shop[0]!.Creature!.Instance;environment.Step(new(Move.Buy,0,0));
        var owned=environment.State.Units.Single(x=>x?.Instance==bought)!;Assert.Equal(50,owned.Bonuses[0]);Assert.True(owned.Shiny);
        environment.Step(new(Move.Reroll));Assert.Equal(50,environment.State.Shop[0]!.Creature!.Bonuses[0]);Assert.Equal(50,owned.Bonuses[0]);
    }

    [Fact]
    public void Common_bug_uses_bargain_bin_first_and_preserves_bug_catcher_for_an_uncommon()
    {
        var environment=Ready("bug-catcher");Bag(environment,new BagEntry("bargain-bin",1));
        environment.State.Shop=[new(new(){SpeciesId="unit-0",Instance=50},null,20),new(new(){SpeciesId="unit-1",Instance=51},null,20),null,null,null];
        Assert.Equal(0,environment.GetObservation().Shop[0]!.Price);
        environment.Step(new(Move.Buy,0,0));Assert.Equal(1000,environment.State.Gold);
        Assert.Equal(1,environment.State.Counters["free-common-used"]);Assert.False(environment.State.Counters.ContainsKey("free-bug-used"));
        environment.Step(new(Move.Buy,1,1));Assert.Equal(1000,environment.State.Gold);Assert.Equal(1,environment.State.Counters["free-bug-used"]);
        environment.State.Shop[0]=new(new(){SpeciesId="unit-0",Instance=52},null,20);
        Assert.Equal(20,environment.GetObservation().Shop[0]!.Price);environment.Step(new(Move.Buy,0,2));Assert.Equal(980,environment.State.Gold);
    }

    [Fact]
    public void Bug_net_and_coupon_discounts_are_stored_and_refresh_does_not_accumulate_them()
    {
        var environment=Ready();Bag(environment,new BagEntry("bug-net",1));Assert.Equal(17,environment.State.Shop[0]!.Price);
        Assert.Equal(3,environment.State.Shop[0]!.Creature!.StoredDiscount);
        var coupon=ItemRules.Definition("coupon")!.Value;
        var rules=Fixture() with {Items=Fixture().Items.Append(new("coupon","Coupon",0,false,0,coupon.Effects,"fixture",coupon.Text)).ToArray()};
        environment=new GameEnvironment(rules);environment.Reset(7,"fixture");Bag(environment,new BagEntry("bug-net",1));
        environment.State.Shop[4]=new(null,"coupon",0);environment.Step(new(Move.BuyItem,4));
        Assert.Equal(14,environment.State.Shop[0]!.Price);Assert.Equal(6,environment.State.Shop[0]!.Creature!.StoredDiscount);
        for(var i=0;i<10;i++)environment.GetObservation();Assert.Equal(6,environment.State.Shop[0]!.Creature!.StoredDiscount);
    }

    [Fact]
    public void Youngster_free_rerolls_carry_and_daily_income_and_item_allowance_reset()
    {
        var environment=Ready("youngster-m");Assert.Equal(3,environment.State.FreeRerolls);
        Bag(environment,new BagEntry("gold-nugget",2),new("membership-card",2));environment.State.ItemUses=0;
        var before=environment.State.Gold;environment.Step(new(Move.Finish),b=>b);
        Assert.Equal(6,environment.State.FreeRerolls);Assert.Equal(before+36,environment.State.Gold);Assert.Equal(3,environment.State.ItemUses);
        environment=new GameEnvironment(Fixture());environment.Reset(1,"lady");Assert.Equal(3,environment.State.Rank);Assert.Equal(40,environment.State.Gold);
        environment=Ready();Bag(environment,new BagEntry("fools-gold",1));environment.Step(new(Move.Reroll));Assert.All(environment.State.Shop,x=>Assert.NotNull(x!.Creature));
    }

    [Fact]
    public void Shopkeeper_modifies_only_the_item_tier_and_gentleman_starts_on_day_four()
    {
        var environment=Ready("shopkeeper");Assert.Equal("higher-tier",environment.State.Shop[4]!.ItemId);
        Assert.Equal(8.5,environment.State.Shop[4]!.Price,8);
        Assert.All(environment.State.Shop.Take(4),x=>Assert.Equal("unit-0",x!.Creature!.SpeciesId));
        environment=Ready("gentleman");Assert.Equal(1,environment.GetShopOdds()[0]);
        for(var day=1;day<4;day++)environment.Step(new(Move.Finish),b=>b);
        Assert.Equal(4,environment.State.Day);Assert.Equal(0,environment.GetShopOdds()[0]);Assert.Equal(0,environment.GetShopOdds()[1]);
    }

    [Fact]
    public void Cosmivore_uses_only_discarded_offers_and_ticket_repeats_each_read_their_own_shelf()
    {
        var rules=Fixture();var effects=RuleCompiler.CompileText("When rerolling the shop, gain 20% of the total stats of the monsters remaining in the shop (excluding Multicast and Cooldown).",Trigger.Ongoing,out var understood);Assert.True(understood);
        var species=rules.Species[0] with {Normal=rules.Species[0].Normal.Select(x=>x with {Effects=effects}).ToArray()};
        rules=rules with {Species=rules.Species.Select(x=>x.Id==species.Id?species:x).ToArray()};
        var environment=new GameEnvironment(rules);environment.Reset(9,"fixture");environment.State.Units[0]=new(){SpeciesId=species.Id};
        environment.State.Shop=[new(new(){SpeciesId="unit-1",Bonuses=[5,3,0,0,0,0,0,9,0]},null,20),null,null,null,new(null,"gray-ticket",0)];
        environment.State.Bag=[new("echo-bell",1)];environment.Step(new(Move.BuyItem,4));
        // First shelf contributes 30 Damage and 3 Heal; the echoed ticket then discards five fresh 25-Damage units.
        Assert.Equal(31,environment.State.Units[0]!.Bonuses[0],8);Assert.Equal(.6,environment.State.Units[0]!.Bonuses[1],8);
        Assert.Equal(0,environment.State.Units[0]!.Bonuses[7]);Assert.Equal(2,environment.State.Counters["items-used"]);
    }

    [Fact]
    public void Purchased_items_and_echoed_uses_have_distinct_triggers_and_merge_watch_is_targeted()
    {
        var rules=Fixture();var reaction=RuleCompiler.CompileText("When you use an item, this gains +24 Damage and Shield.",Trigger.Ongoing,out _);
        var actor=rules.Species[0] with {Normal=rules.Species[0].Normal.Select(x=>x with {Effects=reaction}).ToArray()};
        rules=rules with {Species=rules.Species.Select(x=>x.Id==actor.Id?actor:x).ToArray()};
        var environment=new GameEnvironment(rules);environment.Reset(1,"fixture");environment.State.Units[0]=new(){SpeciesId=actor.Id};
        Bag(environment,new BagEntry("echo-bell",1),new("power-pouch",1));environment.State.Shop[4]=new(null,"fake-coin",0);
        environment.Step(new(Move.BuyItem,4));Assert.Equal(50,environment.State.Units[0]!.Bonuses[0]);Assert.Equal(48,environment.State.Units[0]!.Bonuses[2]);
        Assert.Equal(1,environment.State.Counters["items-bought"]);Assert.Equal(2,environment.State.FreeRerolls);
        environment=Ready();Bag(environment,new BagEntry("silver-watch",1));environment.State.Units[0]=new(){SpeciesId="unit-0",Copies=2};
        environment.State.Shop[0]=new(new(){SpeciesId="unit-0",Instance=99},null,20);environment.Step(new(Move.Buy,0,1));
        Assert.Equal(.12,environment.State.Units[0]!.Bonuses[6],8);Assert.Equal(2,environment.State.Units[0]!.Level);
    }

    [Fact]
    public void Restoring_mid_shop_preserves_offer_bonuses_modifiers_prices_and_rng()
    {
        var environment=Ready("shopkeeper");Bag(environment,new BagEntry("power-bell",1),new("research-notes",1),new("rainbow-pearl",1));
        var saved=environment.State.Clone();var restored=new GameEnvironment(Fixture());restored.Restore(saved);
        Assert.Equal(JsonSerializer.Serialize(environment.GetObservation()),JsonSerializer.Serialize(restored.GetObservation()));
        Assert.Equal(environment.GetLegalActions(),restored.GetLegalActions());
        environment.Step(new(Move.Reroll));restored.Step(new(Move.Reroll));
        Assert.Equal(JsonSerializer.Serialize(environment.State),JsonSerializer.Serialize(restored.State));
    }
}
