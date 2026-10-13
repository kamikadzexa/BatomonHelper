using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ItemSimulationTests
{
    private static RulesManifest ItemFixture()
    {
        var fixture=RuleCompiler.Fixture();
        var species=Enumerable.Range(0,18).Select(i=>fixture.Species[0] with
        {
            Id="unit-"+i, MergeGroup="unit-"+i, Rarity=i%6,
            Types=[new[]{"fire","electric","toxic","water","rock","grass"}[i%6]],
            Normal=fixture.Species[0].Normal.Select(x=>x with {Effects=i==17?[new(Trigger.Bought,Operation.Gold,Target.Self,Amount:7)]:[]}).ToArray(),
            Shiny=fixture.Species[0].Normal
        }).ToArray();
        var names=new[]{"apex-bait","basic-bait","basic-candy","battery-pack","black-feather","black-sludge","blue-ticket","cake","coffee","coupon",
            "crimson-gift","crimson-ticket","dowsing-rod","fake-coin","feast","focus-pill","gold-powder","golden-gift","golden-ticket","gray-chip",
            "gray-ticket","green-stone","green-ticket","hot-pepper","lootbox","lucky-coin","magic-lasso","mystic-pearl","nana-berry","pom-berry",
            "purple-gift","purple-ticket","rare-candy","recruiting-flyer","red-coin","shiny-berry","shiny-pebble","tote-bag","ultra-candy","voucher"};
        var items=names.Select(id=>
        {
            var definition=ItemRules.Definition(id)!.Value;
            Assert.True(ItemRules.TryCompile(id,definition.Text,out var effects));
            return new InventoryDefinition(id,id,0,false,0,effects,"fixture",definition.Text);
        }).ToArray();
        return fixture with {Species=species,Items=items,
            Trinkets=Enumerable.Range(0,12).Select(i=>new InventoryDefinition(i==0?"mysterious-charm":"trinket-"+i,"Trinket",i%6,false,0,[],"fixture",""))
                .Append(new("echo-bell","Echo",5,false,0,ShopRules.Definition("trinkets","echo-bell")!.Value.Effects,"fixture","" )).ToArray()};
    }
    private static GameEnvironment Ready()
    {
        var rules=ItemFixture();var environment=new GameEnvironment(rules);environment.Reset(123,"fixture");
        for(var i=0;i<6;i++)environment.State.Units[i]=new(){SpeciesId="unit-"+i,Instance=environment.State.NextInstance++};
        environment.State.Units[3]!.Level=3;
        environment.State.Shop=[new(environment.State.Units[0]!.Clone(),null,10),new(environment.State.Units[1]!.Clone(),null,10),null,null,null];
        environment.State.Gold=100;
        return environment;
    }
    private static StepResult Buy(GameEnvironment environment,string id,double price=0)
    {
        environment.State.Shop[4]=new(null,id,price);
        return environment.Step(new(Move.BuyItem,4));
    }

    [Fact]
    public void All_forty_current_item_definitions_execute_and_changed_text_is_rejected()
    {
        var fixture=ItemFixture();Assert.Equal(40,fixture.Items.Length);
        foreach(var item in fixture.Items)
        {
            var environment=Ready();var result=Buy(environment,item.Id);
            Assert.False(result.Terminal);Assert.Null(environment.State.Shop[4]?.ItemId);
            Assert.Equal(1,environment.State.Counters["items-used"]);
            Assert.False(ItemRules.TryCompile(item.Id,item.Description+" Changed by a patch.",out _));
            var restored=new GameEnvironment(fixture);restored.Restore(environment.State);
            Assert.Equal(JsonSerializer.Serialize(environment.State),JsonSerializer.Serialize(restored.State));
        }
    }

    [Fact]
    public void Type_and_position_items_leave_bench_and_unrelated_units_unchanged()
    {
        var environment=Ready();environment.State.Units[6]=new(){SpeciesId="unit-0"};
        Buy(environment,"hot-pepper");Assert.Equal(1,environment.State.Units[0]!.Bonuses[(int)Stat.Burn]);
        Assert.Equal(1,environment.State.Units[6]!.Bonuses[(int)Stat.Burn]);
        Assert.Equal(0,environment.State.Units[1]!.Bonuses[(int)Stat.Burn]);
        environment.State.ItemUses=1;Buy(environment,"pom-berry");
        Assert.Equal(8,environment.State.Units[5]!.Bonuses[(int)Stat.Damage]);
        Assert.Equal(0,environment.State.Units[6]!.Bonuses[(int)Stat.Damage]);Assert.Equal(1,environment.State.ItemUses);
    }

    [Fact]
    public void Cake_selects_two_distinct_units_and_candies_only_level_eligible_targets()
    {
        var environment=Ready();Buy(environment,"cake");
        Assert.Equal(2,environment.State.Units.OfType<Unit>().Count(x=>x.Bonuses[0]==5));
        environment.State.ItemUses=1;Buy(environment,"basic-candy");
        var leveled=environment.State.Units.OfType<Unit>().Single(x=>x.Level==2);
        Assert.Contains(leveled.SpeciesId,new[]{"unit-0","unit-1"});
        environment.State.ItemUses=1;Buy(environment,"ultra-candy");Assert.Equal(4,environment.State.Units[3]!.Level);
    }

    [Fact]
    public void Tickets_preserve_locks_and_free_rerolls_while_echo_repeats_zero_price_effects()
    {
        var environment=Ready();environment.State.Locked=true;environment.State.FreeRerolls=7;
        environment.State.Bag=[new("echo-bell",1)];Buy(environment,"golden-ticket");
        Assert.All(environment.State.Shop,x=>Assert.Equal(4,int.Parse(x!.Creature!.SpeciesId[5..])%6));
        Assert.True(environment.State.Locked);Assert.Equal(7,environment.State.FreeRerolls);Assert.Equal(0,environment.State.ItemUses);
        environment.State.ItemUses=1;Buy(environment,"lucky-coin");Assert.Equal(106,environment.State.Gold);Assert.Equal(2,environment.State.ItemUses);
    }

    [Fact]
    public void Coupon_records_actual_discounts_and_transform_keeps_identity_progress_and_bonuses()
    {
        var environment=Ready();Buy(environment,"coupon");
        var offer=environment.State.Shop[0]!;Assert.Equal(7,offer.Price);Assert.Equal(3,offer.Creature!.StoredDiscount);
        var unit=environment.State.Units[0]!;unit.Bonuses[0]=31;unit.AddedTypes=["dragon"];unit.Counters["progress"]=9;unit.Shiny=true;
        environment.State.ItemUses=1;Buy(environment,"green-stone");
        Assert.Same(unit,environment.State.Units[0]);Assert.NotEqual("unit-0",unit.SpeciesId);Assert.Equal(31,unit.Bonuses[0]);
        Assert.Equal(["dragon"],unit.AddedTypes);Assert.Equal(9,unit.Counters["progress"]);Assert.True(unit.Shiny);
    }

    [Fact]
    public void Bought_ability_runs_before_automatic_merge_and_overflow_can_complete_the_merge()
    {
        var environment=Ready();environment.State.Units[0]=new(){SpeciesId="unit-17",Copies=1};
        environment.State.Units[1]=new(){SpeciesId="unit-17",Copies=1};
        for(var i=2;i<10;i++)environment.State.Units[i]=new(){SpeciesId="unit-"+i};
        environment.State.Shop[0]=new(new(){SpeciesId="unit-17",Instance=500},null,10);
        environment.Step(new(Move.Buy,0));
        Assert.Equal(97,environment.State.Gold); // 100 - price 10 + level-1 bought ability 7.
        Assert.Equal(2,environment.State.Units[0]!.Level);Assert.Null(environment.State.Overflow);
        Assert.Equal(Phase.Reward,environment.State.Phase);
    }

    [Fact]
    public void Multiple_gifts_and_overflow_never_overwrite_pending_choices()
    {
        var environment=Ready();environment.State.Bag=[new("echo-bell",1)];Buy(environment,"dowsing-rod");
        Assert.Equal(Phase.Reward,environment.State.Phase);Assert.Single(environment.State.PendingChoices);
        var first=environment.State.Choices[0].DefinitionId;environment.Step(new(Move.Choose,Choice:0));
        Assert.Equal(Phase.Reward,environment.State.Phase);Assert.Contains(environment.State.Bag,x=>x.Id==first);
        var restored=new GameEnvironment(ItemFixture());restored.Restore(environment.State);
        restored.Step(new(Move.Choose,Choice:0));Assert.Equal(Phase.Shop,restored.State.Phase);
        Assert.Equal(2,restored.State.Bag.Where(x=>x.Id!="echo-bell").Sum(x=>x.Quantity));
    }

    [Fact]
    public void Red_coin_invokes_second_chance_once_and_its_chosen_gift_is_queued()
    {
        var environment=Ready();environment.State.Lives=1;Buy(environment,"red-coin");
        Assert.Equal(1,environment.State.Lives);Assert.True(environment.State.SecondChanceUsed);Assert.Equal(Phase.Event,environment.State.Phase);
        environment.Step(new(Move.Choose,Choice:0));Assert.Equal(Phase.Reward,environment.State.Phase);
        environment.Step(new(Move.Choose,Choice:0));environment.State.ItemUses=1;
        Assert.True(Buy(environment,"red-coin").Terminal);
    }

    [Fact]
    public void Coffee_chip_and_lasso_are_consumed_after_one_battle_and_opponent_stays_frozen()
    {
        var rules=ItemFixture();var castSpecies=rules.Species[0] with {Normal=rules.Species[0].Normal.Select(x=>x with
            {Effects=[new(Trigger.BattleStart,Operation.Add,Target.Self,Amount:10,Permanent:true)]}).ToArray()};
        rules=rules with {Species=rules.Species.Select(x=>x.Id==castSpecies.Id?castSpecies:x).ToArray()};
        var environment=new GameEnvironment(rules);environment.Reset(8,"fixture");
        environment.State.Units[0]=new(){SpeciesId=castSpecies.Id};environment.State.ItemUses=3;
        Buy(environment,"coffee");Buy(environment,"gray-chip");Buy(environment,"magic-lasso");
        var copied=new Unit {SpeciesId="unit-1",Instance=876,Level=2,Shiny=true,Bonuses=[3,0,0,0,0,0,0,0,0],AddedTypes=["dragon"]};
        var enemy=new FrozenBoard(1,1,"fixture",[null,copied,null,null,null,null],[],[]);var saved=JsonSerializer.Serialize(enemy);
        var gold=environment.State.Gold;var result=environment.Step(new(Move.Finish),_=>enemy);
        Assert.True(result.Battle!.PlayerWon);Assert.Equal(20,environment.State.Units[0]!.Bonuses[0]);
        Assert.Equal(gold+15+rules.Parameters.DailyIncome[1],environment.State.Gold);
        Assert.DoesNotContain(environment.State.Counters.Keys,x=>x.StartsWith("next-"));
        var grant=environment.State.Units.OfType<Unit>().Single(x=>x.SpeciesId=="unit-1");
        Assert.Equal(2,grant.Level);Assert.True(grant.Shiny);Assert.Equal(3,grant.Bonuses[0]);Assert.Equal(["dragon"],grant.AddedTypes);
        Assert.NotEqual(copied.Instance,grant.Instance);Assert.Equal(saved,JsonSerializer.Serialize(enemy));
    }
}
