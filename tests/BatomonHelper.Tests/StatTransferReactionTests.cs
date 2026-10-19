using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class StatTransferReactionTests
{
    private static Effect[] Effects(string text, Trigger trigger = Trigger.Cast)
    {var effects=RuleCompiler.CompileText(text,trigger,out var understood);Assert.True(understood);return effects;}
    private static Species Species(string id,double? cooldown,Effect[] effects,double damage=0, double shield=0, string[]? types=null, Trigger trigger=Trigger.None) =>
        RuleCompiler.Fixture().Species[0] with {Id=id,MergeGroup=id,Types=types??["fire"],
            Normal=Enumerable.Range(1,4).Select(l=>new LevelDefinition(l,cooldown,[damage*l,0,shield,0,0,0,0,1,0],effects,AbilityTrigger:trigger)).ToArray()};
    private static FrozenBoard Board(params string?[] ids) => new(1,100,"fixture",Enumerable.Range(0,6)
        .Select(i=>i<ids.Length && ids[i] is { } id?new Unit {SpeciesId=id,Instance=i+1}:null).ToArray(),[],[]);
    private static BattleResult Match(Species[] species,FrozenBoard own,FrozenBoard enemy,InventoryDefinition[]? bag=null)
    {
        var rules=RuleCompiler.Fixture() with {Species=species,Trinkets=bag??[]};var combat=new Combat(rules);
        var original=JsonSerializer.Serialize(enemy);var fast=combat.ResolveBattle(own,enemy,1,true);
        var reference=combat.ResolveBattle(own,enemy,1,true,true);
        Assert.Equal(reference.Trace,fast.Trace);Assert.Equal(reference.PlayerHp,fast.PlayerHp,7);Assert.Equal(reference.OpponentHp,fast.OpponentHp,7);
        Assert.Equal(original,JsonSerializer.Serialize(enemy));return fast;
    }

    [Theory]
    [InlineData("When you receive shield, this gains Damage for this battle equal to 50% of the amount shielded.")]
    [InlineData("Whenever your team inflicts Burn, gain +10% Damage this battle.")]
    [InlineData("When allies inflict Poison, this gains +2 Damage permanently.")]
    [InlineData("When an ally levels up, this gains +2 Damage and +3 Heal permanently.")]
    [InlineData("Activate the On Victory abilities of all allies 2 time(s).")]
    [InlineData("Disable abilities of all Ongoing monsters in this row.")]
    [InlineData("Disable abilities of enemy Ongoing monsters in this row.")]
    [InlineData("Gain 50% of the stats of the enemy monster with the highest stats permanently (excluding Multicast and Cooldown).")]
    [InlineData("Has additional Damage equal to 100% of the total Damage of adjacent allies. (Except other Gaiadrasil)")]
    [InlineData("Has additional Damage equal to 100% of the total Damage of your allies. (Except other Quillustrous)")]
    [InlineData("Gain Shield for this battle equal to 2x the Shield of adjacent allies. (Except other Aegistruct)")]
    [InlineData("Gain Poison for this battle equal to the total Poison of your allies. (Except other Miasmaw)")]
    [InlineData("Give the Fire ally behind 2x this monster's Burn for this battle.")]
    [InlineData("Allies gain Damage for this battle equal to their Shield.")]
    [InlineData("When a Toxic ally casts, give it +50% Cooldown Speed for this battle.")]
    [InlineData("On the first cast, gain 2 random non-unique Legendary Trinket(s) for each Legendary ally.")]
    [InlineData("+5 Sell Value permanently. Ongoing Has additional Damage equal to 3 times this monster's Sell Value.")]
    [InlineData("Knockout all Common allies and enemies and gain +2 Damage permanently for each Knockout.")]
    public void Grammars_are_complete_and_reject_unknown_suffixes(string text)
    {Assert.NotEmpty(Effects(text));RuleCompiler.CompileText(text+" Unknown.",Trigger.Cast,out var understood);Assert.False(understood);}

    [Fact]
    public void Highest_stats_copy_excludes_multicast_and_cooldown_and_is_permanent()
    {
        var actor=Species("actor",1,Effects("Gain 50% of the stats of the enemy monster with the highest stats permanently (excluding Multicast and Cooldown)."));
        var decoy=Species("decoy",null,[],10);decoy.Normal[0].Stats[7]=100;decoy.Normal[0].Stats[6]=10;
        var best=Species("best",null,[],20,30);
        var result=Match([actor,decoy,best],Board("actor"),Board("decoy","best") with {MaxHp=1});
        Assert.Equal(10,result.PersistentTeam[0]!.Bonuses[0]);Assert.Equal(15,result.PersistentTeam[0]!.Bonuses[2]);
        Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[6]);Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[7]);
    }

    [Fact]
    public void Allied_damage_includes_inventory_buffs_and_excludes_other_members_of_the_source_species()
    {
        var actor=Species("gaiadrasil",1,Effects("Has additional Damage equal to 100% of the total Damage of adjacent allies. (Except other Gaiadrasil)",Trigger.Ongoing));
        var other=Species("other",null,[],10);var crown=InventoryRules.Definition("power-crown")!.Value;
        var inventory=new InventoryDefinition("power-crown","Crown",0,false,0,crown.Effects,"fixture",crown.Text);
        var own=Board("gaiadrasil","other",null,"gaiadrasil") with {Bag=[new("power-crown",1,1)]};
        own.Team[3]!.Bonuses[0]=100;
        var result=Match([actor,other],own,own with {Bag=[],MaxHp=25,Team=new Unit?[6]},[inventory]);
        Assert.Equal(1,result.Seconds);Assert.Equal(-105,result.OpponentHp); // Both Gaiadrasil cast: 30 and 100; clones do not feed each other.
    }

    [Fact]
    public void Allied_shield_transfer_uses_buffed_stats_and_excludes_the_named_species()
    {
        var actor=Species("aegistruct",1,Effects("Gain Shield for this battle equal to 2x the Shield of adjacent allies. (Except other Aegistruct)"));
        var other=Species("other",null,[],shield:10);var attacker=Species("attacker",1,[],1000);
        var shield=new InventoryDefinition("shield","Shield",0,false,0,[new(Trigger.Ongoing,Operation.Add,Target.All,Stat.Shield,5)],"fixture","");
        var own=Board("aegistruct","other",null,"aegistruct") with {MaxHp=1,Bag=[new("shield",1,1)]};own.Team[3]!.Bonuses[2]=100;
        var result=Match([actor,other,attacker],own,Board("attacker"),[shield]);
        Assert.Equal(-859,result.PlayerHp); // Two casters apply 35 and 105 Shield; source clones are excluded from transfer.
        Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[2]);
    }

    [Fact]
    public void Burn_transfer_sees_dynamic_source_burn_and_preserves_target_type_filter()
    {
        var e=Effects("Give the Fire ally behind 2x this monster's Burn for this battle.")
            .Concat(Effects("Has an additional +5 Burn for each Trinket that you own.",Trigger.Ongoing)).ToArray();
        var actor=Species("actor",1,e);var ally=Species("ally",1,[]);
        var junk=new InventoryDefinition("junk","Junk",0,false,0,[],"fixture","");var own=Board("ally","actor") with {Bag=[new("junk",2,1)]};
        var result=Match([actor,ally],own,own with {Bag=[],MaxHp=5,Team=new Unit?[6]},[junk]);
        Assert.Equal(1,result.Seconds);Assert.Equal(-5,result.OpponentHp);
        // The left recipient casts earlier on this tick, so the new buff is
        // available on its next cast rather than retroactively changing this one.
        var probe=new CombatProbe {Duration=1};
        new Combat(RuleCompiler.Fixture() with {Species=[actor,ally],Trinkets=[junk]})
            .ResolveBattle(own,own with {Bag=[],Team=new Unit?[6]},1,probe:probe);
        Assert.Equal(20,probe.Frames[^1].Units.Single(unit=>unit.Slot==0).Stats[(int)Stat.Burn]);
        Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[3]);
    }

    [Fact]
    public void Each_recipient_copies_its_own_shield_instead_of_the_casters_shield()
    {
        var actor=Species("actor",1,Effects("Allies gain Damage for this battle equal to their Shield."));
        var a=Species("a",1,[],shield:5);var b=Species("b",1,[],shield:20);var own=Board("actor","a","b");
        var result=Match([actor,a,b],own,own with {MaxHp=24,Team=new Unit?[6]});
        Assert.Equal(1,result.Seconds);Assert.Equal(-1,result.OpponentHp);Assert.Equal(0,result.PersistentTeam[1]!.Bonuses[0]);
    }

    [Fact]
    public void Toxic_cast_reaction_targets_the_event_actor_and_changes_its_next_cooldown()
    {
        var watcher=Species("watcher",null,Effects("When a Toxic ally casts, give it +50% Cooldown Speed for this battle.",Trigger.Ongoing));
        var actor=Species("actor",1,[],1,types:["toxic"]);var own=Board("watcher","actor");
        var result=Match([watcher,actor],own,own with {MaxHp=2,Team=new Unit?[6]});
        Assert.Equal(1.67,result.Seconds,8);Assert.Equal(0,result.PersistentTeam[1]!.Bonuses[6]);
    }

    [Fact]
    public void Team_shield_and_burn_reactions_include_the_caster_and_affect_later_casts()
    {
        var shield=Species("shield",1,Effects("When you receive shield, this gains Damage for this battle equal to 50% of the amount shielded.",Trigger.Ongoing),shield:10);
        var own=Board("shield");var result=Match([shield],own,own with {MaxHp=4,Team=new Unit?[6]});
        Assert.Equal(2,result.Seconds);Assert.Equal(-1,result.OpponentHp);Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[0]);
        var burn=Species("burn",1,Effects("Whenever your team inflicts Burn, gain +10% Damage this battle.",Trigger.Ongoing),10);
        foreach(var level in burn.Normal)level.Stats[3]=1;
        own=Board("burn");result=Match([burn],own,own with {MaxHp=21,Team=new Unit?[6]});
        Assert.Equal(2,result.Seconds);Assert.Equal(-2,result.OpponentHp);Assert.Equal(0,result.PersistentTeam[0]!.Bonuses[0]);
    }

    [Fact]
    public void Temporary_level_ups_notify_allies_and_their_permanent_reactions_survive()
    {
        var actor=Species("actor",1,Effects("On the first cast, level up the ally above for this battle (max level 4)."),1);
        var above=Species("above",null,[]);var watcher=Species("watcher",null,Effects("When an ally levels up, this gains +2 Damage and +3 Heal permanently.",Trigger.Ongoing));
        var own=Board("above","watcher",null,"actor");var result=Match([actor,above,watcher],own,own with {MaxHp=1,Team=new Unit?[6]});
        Assert.Equal(1,result.PersistentTeam[0]!.Level);Assert.Equal(2,result.PersistentTeam[1]!.Bonuses[0]);Assert.Equal(3,result.PersistentTeam[1]!.Bonuses[1]);
    }

    [Fact]
    public void Disabling_ongoing_abilities_preserves_base_casts_and_includes_reactive_ongoing_abilities()
    {
        var actor=Species("actor",1,Effects("Disable abilities of enemy Ongoing monsters in this row.",Trigger.BattleStart),20);
        var passive=Species("passive",1,[new(Trigger.Ongoing,Operation.Add,Target.Self,Amount:10)],3,trigger:Trigger.Ongoing);
        var reactive=Species("reactive",1,[new(Trigger.AllyCast,Operation.Add,Target.Self,Amount:7)],1,trigger:Trigger.Ongoing);
        var result=Match([actor,passive,reactive],Board("actor"),Board("passive","reactive") with {MaxHp=1});
        Assert.Equal(96,result.PlayerHp);Assert.Equal(1,result.Seconds);
    }

    [Fact]
    public void Defeat_can_activate_victory_abilities_of_knocked_out_allies()
    {
        var dead=Species("dead",1,Effects("Knockout self.").Concat(Effects("+7 Damage permanently.",Trigger.Victory)).ToArray());
        var actor=Species("actor",null,Effects("Activate the On Victory abilities of all allies 2 time(s).",Trigger.Defeat));
        var enemy=Species("enemy",2,[],1000);var result=Match([dead,actor,enemy],Board("dead","actor"),Board("enemy"));
        Assert.False(result.PlayerWon);Assert.Equal(14,result.PersistentTeam[0]!.Bonuses[0]);
    }

    [Fact]
    public void A_first_cast_gift_with_no_legendary_allies_grants_nothing()
    {
        var actor=Species("actor",1,Effects("On the first cast, gain 2 random non-unique Legendary Trinket(s) for each Legendary ally."),1) with {Rarity=4};
        var gift=new InventoryDefinition("gift","Gift",4,false,0,[],"fixture","");var own=Board("actor");
        Assert.Empty(Match([actor],own,own with {MaxHp=1,Team=new Unit?[6]},[gift]).Bag);
        own=Board("actor","actor");var result=Match([actor],own,own with {MaxHp=1,Team=new Unit?[6]},[gift]);
        Assert.Equal(4,result.Bag.Length);
    }
}
