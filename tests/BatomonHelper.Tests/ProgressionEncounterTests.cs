using BatomonHelper.Simulation;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ProgressionEncounterTests
{
    private static readonly string[] EventIds = ["baby-basilisk", "bug-collector", "dragon-tamer", "dragons-nest",
        "dryad-statue", "dryad-statue-oshima", "fossil-discovery", "genius-inventor", "meteor-crash", "pond-fairy",
        "professor-encounter", "rainbow-berry", "second-chance", "traveling-merchant"];

    // Synthetic effects and stats deliberately isolate event plumbing from unimplemented game abilities.
    private static RulesManifest Fixture(SimulationProfile? profile = null)
    {
        profile ??= SimulationProfile.UserPantra();
        var compiled = profile.Compile(); var basis = RuleCompiler.Fixture();
        var events = EventIds.Select(x => EncounterRules.Definition("events/" + x)!).ToArray();
        var units = events.SelectMany(x => x.Choices).SelectMany(x => x.Effects)
            .Where(x => x.Operation == Operation.GrantUnit && x.DefinitionId is not null).Select(x => x.DefinitionId!)
            .Concat(["unit-0", "unit-1", "unit-2", "unit-3", "unit-4", "unit-5", "foreign"]).Distinct()
            .Select(id => basis.Species[0] with { Id = id, MergeGroup = id,
                Rarity = id.StartsWith("unit-") ? int.Parse(id[5..]) : 2,
                ShopEligible = id.StartsWith("unit-"), Pantra = id != "foreign", ForeignEligible = id == "foreign",
                Shiny = basis.Species[0].Normal }).ToArray();
        var trinkets = events.SelectMany(x => x.Choices).SelectMany(x => x.Effects)
            .Where(x => x.Operation == Operation.GrantTrinket).Select(x => x.DefinitionId!).Distinct()
            .Select(id => new InventoryDefinition(id, id, 2, false, 0, [], "fixture", "Synthetic event reward"))
            .Concat(Enumerable.Range(0, 6).Select(tier => new InventoryDefinition("gift-" + tier, "Gift", tier, false, 0, [], "fixture", "Synthetic gift")))
            .ToArray();
        return basis with { Species = units, Trinkets = trinkets, Profile = profile, Parameters = compiled.Parameters,
            Evidence = compiled.Evidence, Encounters = events };
    }
    private static GameEnvironment Ready(SimulationProfile? profile = null)
    {
        var env = new GameEnvironment(Fixture(profile)); env.Reset(123, "fixture");
        env.State.Units[0] = new() { Instance = 50, SpeciesId = "unit-0" };
        return env;
    }
    private static void ResolveChoices(GameEnvironment env)
    {
        for (var limit = 0; env.State.Phase is Phase.Event or Phase.Reward or Phase.TrainerChoice; limit++)
        {
            Assert.True(limit < 20); var actions = env.GetLegalActions(); Assert.NotEmpty(actions);
            env.Step(actions.Last());
        }
    }

    [Fact]
    public void Observed_tables_are_exact_and_every_missing_value_is_labelled()
    {
        var profile = SimulationProfile.UserPantra(); var (parameters, evidence) = profile.Compile();
        foreach (var point in profile.Observations)
        {
            Assert.Equal(point.Hp, parameters.DailyHp[point.Day - 1]);
            Assert.Equal(30+5*(point.Day-1), parameters.DailyIncome[point.Day - 1]);
            Assert.Equal(EvidenceKind.UserProvided, parameters.HpEvidence![point.Day - 1].Kind);
            Assert.Equal(EvidenceKind.UserProvided, parameters.IncomeEvidence![point.Day - 1].Kind);
        }
        foreach (var (day, hp, income) in new[] { (6, 4050d, 55d), (10, 18250d, 75d), (12, 38600d, 85d), (16, 106800d, 105d) })
        {
            Assert.Equal(hp, parameters.DailyHp[day - 1]); Assert.Equal(income, parameters.DailyIncome[day - 1]);
            Assert.Equal(EvidenceKind.Guessed, parameters.HpEvidence![day - 1].Kind);
            Assert.Equal(EvidenceKind.UserProvided, parameters.IncomeEvidence![day - 1].Kind);
        }
        Assert.Equal(31, evidence.Count(x => x.Kind == EvidenceKind.Guessed));
        Assert.True(parameters.Usable); Assert.False(parameters.Verified);
        Assert.Contains("\"Guessed\"", JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void Authorizing_numeric_guesses_does_not_authorize_missing_handlers()
    {
        var rules = Fixture(); Assert.True(rules.Ready); Assert.False(rules.FullFidelityReady);
        Assert.False((rules with { Issues = [new("unimplemented", "Ability", "Missing code", "fixture")] }).Ready);
        Assert.False(Fixture(SimulationProfile.UserPantra() with { AllowGuesses = false }).Ready);
        Assert.Throws<InvalidDataException>(() => (SimulationProfile.UserPantra() with { CombatTickSeconds = .03 }).Compile());
        Assert.Throws<InvalidDataException>(() => (SimulationProfile.UserPantra() with { Observations = [new(1, 300, 30), new(1, 500, 35)] }).Compile());
        Assert.Throws<InvalidDataException>(() => (SimulationProfile.UserPantra() with { EvidenceOverrides = [new("unknown", EvidenceKind.Wiki, "url", "reason")] }).Compile());
    }

    [Fact]
    public void Initial_hp_is_separate_from_run_lives_and_daily_gold_carries_forward()
    {
        var env = Ready(); Assert.Equal(300, env.State.MaxHp); Assert.Equal(10, env.State.Lives); Assert.Equal(30, env.State.Gold);
        env.State.Gold = 17; env.Step(new(Move.Finish), b => b);
        Assert.Equal(2, env.State.Day); Assert.Equal(500, env.State.MaxHp); Assert.Equal(52, env.State.Gold);
    }

    [Fact]
    public void Every_encounter_choice_executes_and_target_choices_are_explicit()
    {
        foreach (var id in EventIds)
        for (var choice = 0; choice < 3; choice++)
        {
            var env = Ready(); env.OpenEncounter("events/" + id);
            var action = env.GetLegalActions().First(x => x.Choice == choice);
            Assert.Equal(env.State.Choices[choice].TargetRule is null ? -1 : 0, action.Target);
            env.Step(action); ResolveChoices(env);
            Assert.Equal(Phase.Shop, env.State.Phase);
            Assert.Contains(env.State.DayChoices, x => x.StartsWith(EncounterRules.Definition("events/" + id)!.Name + ":"));
        }
    }

    [Fact]
    public void Target_filters_reject_ineligible_levels_types_and_shiny_forms()
    {
        var env = Ready(); env.State.Units[1] = new() { SpeciesId = "unit-1", Level = 2 };
        env.OpenEncounter("events/meteor-crash");
        Assert.Equal([0], env.GetLegalActions().Where(x => x.Choice == 0).Select(x => x.Target));
        env.Step(env.GetLegalActions().First(x => x.Choice == 0)); Assert.Equal(2, env.State.Units[0]!.Level);
        env.State.Units[0]!.AddedTypes = ["grass"]; env.OpenEncounter("events/dryad-statue");
        Assert.Equal([1], env.GetLegalActions().Where(x => x.Choice == 0).Select(x => x.Target));
        env.Step(env.GetLegalActions().First(x => x.Choice == 0)); Assert.Contains("grass", env.State.Units[1]!.AddedTypes);
        env.State.Units[0]!.Shiny = true; env.OpenEncounter("events/rainbow-berry");
        Assert.DoesNotContain(env.GetLegalActions(), x => x.Choice == 0 && x.Target == 0);
    }

    [Fact]
    public void Normal_events_use_supplied_days_and_tiers_and_hide_incoming_shop()
    {
        var rules = Fixture(); var env = Ready();
        Assert.DoesNotContain(env.GetEligibleEncounters(3), x => x.Id.EndsWith("oshima"));
        for (var day = 2; day <= 10; day++)
        {
            env.Step(new(Move.Finish), b => b);
            var scheduled = rules.Profile!.Events.FirstOrDefault(x => x.Day == day);
            if (scheduled is not null)
            {
                Assert.Equal(Phase.Event, env.State.Phase); Assert.True(env.State.PendingShopStart);
                Assert.All(env.GetObservation().Shop, x => Assert.Null(x));
                Assert.Equal(scheduled.Tier, rules.Encounters!.Single(x => x.Name == env.State.CurrentEvent).Tier);
                var restored = new GameEnvironment(rules); restored.Restore(env.State);
                var action = env.GetLegalActions().Last(); env.Step(action); restored.Step(action);
                Assert.Equal(JsonSerializer.Serialize(env.State), JsonSerializer.Serialize(restored.State));
                ResolveChoices(env);
            }
            else Assert.Equal(Phase.Shop, env.State.Phase);
        }
    }

    [Fact]
    public void Second_chance_and_its_nested_gift_precede_a_same_day_normal_event()
    {
        var env = Ready(); env.State.Day = 2; env.State.Lives = 1; env.State.Units[0] = null;
        env.Step(new(Move.Finish), b => b with { Team = [new() { SpeciesId = "unit-0" }, null, null, null, null, null] });
        Assert.Equal(3, env.State.Day); Assert.Equal(1, env.State.Lives); Assert.True(env.State.SecondChanceUsed);
        Assert.Equal("Second Chance", env.State.CurrentEvent); Assert.True(env.State.PendingShopStart);
        env.Step(new(Move.Choose, Choice: 0)); Assert.Equal(Phase.Reward, env.State.Phase);
        Assert.All(env.State.Choices, c => Assert.Equal("gift-3", c.DefinitionId));
        env.Step(new(Move.Choose, Choice: 0)); Assert.Equal(Phase.Event, env.State.Phase);
        Assert.NotEqual("Second Chance", env.State.CurrentEvent); Assert.True(env.State.PendingShopStart);
        ResolveChoices(env); Assert.False(env.State.PendingShopStart); Assert.Equal(Phase.Shop, env.State.Phase);
        Assert.Equal(3, env.State.DayChoices.Length);
    }

    [Fact]
    public void Recombination_is_delayed_and_preserves_identity_counters_and_permanent_stats()
    {
        var env = Ready(SimulationProfile.UserPantra() with { Events = [] }); var unit = env.State.Units[0]!;
        unit.Shiny = true; unit.Bonuses[0] = 8; unit.Counters["progress"] = 7;
        env.OpenEncounter("events/genius-inventor"); env.Step(new(Move.Choose, Choice: 0));
        Assert.Equal("unit-0", unit.SpeciesId); Assert.Single(env.State.PendingShopEffects);
        env.Step(new(Move.Finish), b => b);
        var changed = env.State.Units[0]!; Assert.Equal("unit-1", changed.SpeciesId);
        Assert.Equal(50, changed.Instance); Assert.True(changed.Shiny); Assert.Equal(8, changed.Bonuses[0]);
        Assert.Equal(7, changed.Counters["progress"]); Assert.Empty(env.State.PendingShopEffects);
    }

    [Fact]
    public void Foreign_reward_enters_only_the_active_runs_shop_pool()
    {
        var env = Ready(); var other = Ready(); env.OpenEncounter("events/traveling-merchant");
        env.Step(new(Move.Choose, Choice: 0)); Assert.Equal(["foreign"], env.State.ForeignPool);
        Assert.Contains(env.State.Units, x => x?.SpeciesId == "foreign"); Assert.Empty(other.State.ForeignPool);
        env.State.Gold = 1000; env.State.Rank = 15; var seen = false;
        for (var roll = 0; roll < 80; roll++) { env.Step(new(Move.Reroll)); seen |= env.State.Shop.Any(x => x?.Creature?.SpeciesId == "foreign"); }
        Assert.True(seen);
    }
}
