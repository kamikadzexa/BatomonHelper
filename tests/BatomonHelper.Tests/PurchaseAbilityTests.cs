using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class PurchaseAbilityTests
{
    private static RulesManifest Fixture(string text, Trigger trigger = Trigger.Bought)
    {
        var basis = RuleCompiler.Fixture(); var effects = RuleCompiler.CompileText(text, trigger, out var understood);
        Assert.True(understood);
        var actor = basis.Species[0] with { Id = "actor", MergeGroup = "actor", Normal = basis.Species[0].Normal.Select(x => x with { Effects = effects }).ToArray() };
        var plain = basis.Species[0] with { Id = "plain", MergeGroup = "plain", Types = ["rock"] };
        var flying = plain with { Id = "flying", MergeGroup = "flying", Types = ["flying"] };
        var toxic = plain with { Id = "toxic", MergeGroup = "toxic", Types = ["toxic"] };
        return basis with { Species = [actor, plain, flying, toxic] };
    }
    private static GameEnvironment Ready(RulesManifest rules)
    { var env = new GameEnvironment(rules); env.Reset(123, "fixture"); env.State.Gold = 1000; return env; }
    private static void Offer(GameEnvironment env, string id, int index, long instance) => env.State.Shop[index] = new(new() { SpeciesId = id, Instance = instance }, null, 10);

    [Theory]
    [InlineData("Give all monsters in the shop +5 Damage for the rest of the run.", Trigger.Bought, Target.Shop)]
    [InlineData("Give all monsters in the shop, on your team, and on your bench +5 Damage for the rest of the run.", Trigger.Bought, Target.ShopAndAll)]
    [InlineData("Give Toxic monsters in the shop and on your team +2 Poison for the rest of the run.", Trigger.Bought, Target.ShopAndTeam)]
    [InlineData("Give Flying monsters in the shop +2 Multicast for the rest of the run.", Trigger.Bought, Target.Shop)]
    [InlineData("At the start of each day, monsters in your shop gain +60 Damage.", Trigger.Ongoing, Target.Shop)]
    public void Full_shop_grammar_preserves_target_scope_and_rejects_partial_matches(string text, Trigger trigger, Target target)
    {
        var effects = RuleCompiler.CompileText(text, trigger, out var understood); Assert.True(understood);
        Assert.Equal(target, Assert.Single(effects).Target);
        RuleCompiler.CompileText(text + " Then do an unknown effect.", trigger, out understood); Assert.False(understood);
    }

    [Fact]
    public void Shop_only_buffs_preserve_the_purchased_offer_and_do_not_reapply_on_refresh()
    {
        var env = Ready(Fixture("Give all monsters in the shop +5 Damage for the rest of the run."));
        Offer(env, "actor", 0, 100); Offer(env, "plain", 1, 101);
        env.Step(new(Move.Buy, 0, 0)); Assert.Equal(0, env.State.Units[0]!.Bonuses[0]);
        env.GetObservation(); env.GetLegalActions(); Assert.Equal(5, env.State.Shop[1]!.Creature!.Bonuses[0]);
        env.Step(new(Move.Buy, 1, 1)); Assert.Equal(5, env.State.Units[1]!.Bonuses[0]);
        env.Step(new(Move.Reroll)); Assert.All(env.State.Shop.OfType<Offer>(), x => Assert.Equal(0, x.Creature!.Bonuses[0]));
    }

    [Fact]
    public void Team_shop_and_bench_scopes_are_distinct()
    {
        foreach (var all in new[] { false, true })
        {
            var text = all ? "Give all monsters in the shop, on your team, and on your bench +5 Damage for the rest of the run."
                : "Give Toxic monsters in the shop and on your team +5 Poison for the rest of the run.";
            var env = Ready(Fixture(text));
            env.State.Units[1] = new() { SpeciesId = "toxic", Instance = 50 };
            env.State.Units[6] = new() { SpeciesId = "toxic", Instance = 51 };
            Offer(env, "actor", 0, 100); Offer(env, "toxic", 1, 101); Offer(env, "plain", 2, 102);
            env.Step(new(Move.Buy, 0, 0)); var stat = all ? 0 : 4;
            Assert.Equal(5, env.State.Units[1]!.Bonuses[stat]); Assert.Equal(all ? 5 : 0, env.State.Units[6]!.Bonuses[stat]);
            Assert.Equal(5, env.State.Shop[1]!.Creature!.Bonuses[stat]); Assert.Equal(all ? 5 : 0, env.State.Shop[2]!.Creature!.Bonuses[stat]);
        }
    }

    [Fact]
    public void Contingent_purchase_buffs_survive_restore_skip_ineligible_purchases_and_expire_after_use()
    {
        var rules = Fixture("Give the next Flying monster you buy +2 Multicast. Lost if sold."); var env = Ready(rules);
        Offer(env, "actor", 0, 100); env.Step(new(Move.Buy, 0, 0)); Assert.Single(env.State.PendingPurchaseBuffs);
        var restored = new GameEnvironment(rules); restored.Restore(env.State);
        foreach (var run in new[] { env, restored })
        {
            Offer(run, "plain", 1, 101); run.Step(new(Move.Buy, 1, 1)); Assert.Single(run.State.PendingPurchaseBuffs);
            Offer(run, "flying", 2, 102); run.Step(new(Move.Buy, 2, 2)); Assert.Equal(2, run.State.Units[2]!.Bonuses[7]);
            Assert.Empty(run.State.PendingPurchaseBuffs);
            Offer(run, "flying", 3, 103); run.Step(new(Move.Buy, 3, 3)); Assert.Equal(0, run.State.Units[3]!.Bonuses[7]);
        }
        Assert.Equal(JsonSerializer.Serialize(env.State), JsonSerializer.Serialize(restored.State));
    }

    [Fact]
    public void Selling_the_source_cancels_its_pending_bonus_and_typing_is_added_before_poison()
    {
        var rules = Fixture("Give the next monster you buy Toxic typing and +8 Poison. Lost if sold.");
        foreach (var sell in new[] { false, true })
        {
            var env = Ready(rules); Offer(env, "actor", 0, 100); env.Step(new(Move.Buy, 0, 0));
            if (sell) env.Step(new(Move.Sell, 0));
            Offer(env, "plain", 1, 101); env.Step(new(Move.Buy, 1, 1));
            Assert.Equal(sell ? 0 : 8, env.State.Units[1]!.Bonuses[4]);
            Assert.Equal(sell ? [] : new[] { "toxic" }, env.State.Units[1]!.AddedTypes);
            Assert.Empty(env.State.PendingPurchaseBuffs);
        }
    }

    [Fact]
    public void Merging_the_source_retains_pending_bonus_until_a_later_purchase()
    {
        var env = Ready(Fixture("Give the next Flying monster you buy +2 Multicast. Lost if sold."));
        Offer(env, "actor", 0, 100); env.Step(new(Move.Buy, 0, 0));
        env.State.Units[1] = new() { SpeciesId = "actor", Instance = 101 };
        env.Step(new(Move.Merge, 0, 1)); Assert.Equal(101, Assert.Single(env.State.PendingPurchaseBuffs).SourceInstance);
        Offer(env, "flying", 2, 102); env.Step(new(Move.Buy, 2, 2)); Assert.Equal(2, env.State.Units[2]!.Bonuses[7]);
    }

    [Fact]
    public void Daily_shop_bonus_affects_the_fresh_or_locked_shop_once_each_day()
    {
        foreach (var locked in new[] { false, true })
        {
            var env = Ready(Fixture("At the start of each day, monsters in your shop gain +60 Damage.", Trigger.Ongoing));
            env.State.Units[0] = new() { SpeciesId = "actor", Instance = 100 };
            var first = env.State.Shop[0]!.Creature!.Instance;
            if (locked) env.Step(new(Move.Lock));
            env.Step(new(Move.Finish), b => b);
            Assert.Equal(locked, first == env.State.Shop[0]!.Creature!.Instance);
            env.GetObservation(); env.GetLegalActions(); Assert.All(env.State.Shop, x => Assert.Equal(60, x!.Creature!.Bonuses[0]));
            Assert.Equal(0, env.State.Units[0]!.Bonuses[0]);
            env.Step(new(Move.Reroll)); Assert.All(env.State.Shop, x => Assert.Equal(0, x!.Creature!.Bonuses[0]));
        }
    }
}
