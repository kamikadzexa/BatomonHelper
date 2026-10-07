using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ShoppingAdviceTests
{
    private static readonly MonsterRules Rules = new(["bug"], [], [new(1, 3.5m, 2, [new(VisibleStatKind.Damage, 15)])], [],
        ImmutableDictionary<int, string>.Empty, ImmutableDictionary<int, string>.Empty, null, "a", "https://batomon.com/batomon/a", default)
    { AbilityTrigger = "On Cast" };
    private static readonly CatalogSnapshot Catalog = CatalogSnapshot.Empty with
    {
        DeclaredSteamBuild = "test", Trainers = [new("trainer", "Trainer", "Your monsters gain Shield.")],
        Monsters = [new("a", "Alpha", Rarity.Common, null, ["starter"]) { Rules = Rules, Regions = ["Jinto region"] },
            new("b", "Beta", Rarity.Common, null, ["starter"]) { Rules = Rules with { MergeGroup = "b" }, Regions = ["Pantra region"] },
            new("evolved", "Evolved", Rarity.Rare, "a", ["starter"]) { Rules = Rules with { MergeGroup = "evolved" }, Regions = ["Jinto region"] }],
        Trinkets = [new("unique", "Unique", Rarity.Common, "Your monsters have +10% Cooldown Speed.", true)]
    };
    private static readonly StrategyKnowledge Knowledge = new(1, "test", null, null, default,
        [new("mechanics/monster-pool", "Shop", "https://batomon.com/mechanics/monster-pool", "Exact rules\nBuying costs the captured price.\nCurrent build examples\nOther text.")]);
    private static BoardSlot Empty(int index) => new(index, SlotOccupancy.Empty, Stats: []);
    private static BoardSlot Creature(int index, string id, int level = 1) => new(index, SlotOccupancy.Creature, id, ShinyState.Normal, level, [new(VisibleStatKind.Damage, 15)]);
    private static ShoppingObservation Observation() => new()
    {
        Resolved = true, Panel = ShoppingPanel.Shop, InventoryResolved = true,
        Board = new() { TrainerId = "trainer", Day = 1, Lives = 10, Wins = 0, WinTarget = 10, TeamHp = 300, Gold = 30, ShopRank = 1,
            RarityOdds = ShopRankOdds.ForRank(1), FreeRerolls = 1, ShopLocked = false,
            Team = Enumerable.Range(0, 6).Select(Empty).ToImmutableArray(), Bench = Enumerable.Range(0, 4).Select(Empty).ToImmutableArray(),
            Shop = [Creature(0, "a") with { Price = 10, Affordable = true }, Creature(1, "b") with { Price = 15, Affordable = true }, Empty(2), Empty(3), Empty(4)] }
    };

    [Fact]
    public void Pool_context_combines_capture_and_marks_and_tracks_only_meaningful_changes()
    {
        var o = Observation(); var board = o.Board! with { Team = o.Board.Team.SetItem(0, Creature(0, "evolved", 4)) };
        var displayed = board.ToShopOddsSnapshot(Catalog) with { RegionFilter = MonsterRegionFilter.Jinto, ShopRank = 4,
            RarityOdds = ShopRankOdds.ForRank(4), ItemAvailable = true };
        var context = ShoppingAdviceContext.Create(Catalog, displayed, board, ["b"], ["unique"]);
        Assert.Equal(ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "a", "b", "evolved"), context.LevelThreeMonsterIds);
        Assert.Empty(context.Odds.MonsterOdds);
        Assert.False(Assert.Single(context.Odds.TrinketOdds).IsEligible);
        Assert.Contains(context.Assumptions, a => a.Contains("Manual odds"));
        Assert.Contains(context.Assumptions, a => a.Contains("Manual item-slot"));
        Assert.Equal(context.Revision, (context with { Snapshot = context.Snapshot with { CapturedAt = DateTimeOffset.MaxValue } }).Revision);
        var other = ShoppingAdviceContext.Create(Catalog, displayed with { RegionFilter = MonsterRegionFilter.Pantra }, board, [], []);
        Assert.NotEqual(context.Revision, other.Revision);
        Assert.NotEqual((o with { AdviceContext = context }).Revision, (o with { AdviceContext = other }).Revision);
        Assert.Equal(o.Board!.Gold, (o with { AdviceContext = context }).Board!.Gold);
    }

    [Fact]
    public void Sequences_account_for_gold_slots_consumed_offers_and_hidden_effects()
    {
        var o = Observation(); var actions = new LegalActionGenerator().Generate(o, Catalog);
        var first = actions.Single(a => a.Kind == ActionKind.BuyCreature && a.Source!.Index == 0 && a.Target == new SlotAddress(SlotArea.Team, 0));
        var second = actions.Single(a => a.Kind == ActionKind.BuyCreature && a.Source!.Index == 1 && a.Target == new SlotAddress(SlotArea.Team, 1));
        Assert.True(ShoppingSequenceValidator.CanFollow(o, Catalog, first, second));
        Assert.False(ShoppingSequenceValidator.CanFollow(o, Catalog, first, first));
        Assert.False(ShoppingSequenceValidator.CanFollow(o, Catalog, first, second with { Target = first.Target }));
        Assert.False(ShoppingSequenceValidator.CanFollow(o with { InventoryResolved = false }, Catalog, first, second));
        Assert.False(ShoppingSequenceValidator.CanFollow(o with { Board = o.Board! with { Gold = 20 } }, Catalog, first, second));
        var bought = Catalog with { Monsters = Catalog.Monsters.SetItem(0, Catalog.Monsters[0] with { Rules = Rules with { AbilityTrigger = "On Bought" } }) };
        Assert.False(ShoppingSequenceValidator.CanFollow(o, bought, first, second));
        var merging = o with { Board = o.Board! with { Bench = o.Board.Bench.SetItem(0, Creature(0, "a")) } };
        Assert.False(ShoppingSequenceValidator.CanFollow(merging, Catalog, first, second));
        var purchaseTrinket = Catalog with { Trinkets = [Catalog.Trinkets[0] with { RuleText = "When a creature is bought, change shop prices." }] };
        var owned = ShoppingAdviceContext.Create(purchaseTrinket, o.Board!.ToShopOddsSnapshot(purchaseTrinket), o.Board, [], ["unique"]);
        Assert.False(ShoppingSequenceValidator.CanFollow(o with { AdviceContext = owned }, purchaseTrinket, first, second));
        Assert.False(ShoppingSequenceValidator.CanFollow(o, Catalog, actions.Single(a => a.Kind == ActionKind.Reroll), second));
    }

    [Theory]
    [InlineData(MonsterRegionFilter.All)]
    [InlineData(MonsterRegionFilter.Jinto)]
    [InlineData(MonsterRegionFilter.Pantra)]
    public void Shared_context_retains_exact_odds_for_each_manual_scenario(MonsterRegionFilter region)
    {
        var board = Observation().Board!;
        foreach (var item in new[] { true, false })
        foreach (var rank in new[] { 1, 4 })
        {
            var snapshot = board.ToShopOddsSnapshot(Catalog) with { RegionFilter = region, ItemAvailable = item,
                ShopRank = rank, RarityOdds = ShopRankOdds.ForRank(rank) };
            var context = ShoppingAdviceContext.Create(Catalog, snapshot, board, ["b"], ["unique"]);
            var expected = new OddsCalculator().Calculate(Catalog, snapshot, new(context.LevelThreeMonsterIds, context.OwnedUniqueTrinketIds));
            Assert.Equal(expected.MonsterOdds.ToArray(), context.Odds.MonsterOdds.ToArray());
            Assert.Equal(expected.ItemOdds.ToArray(), context.Odds.ItemOdds.ToArray());
            Assert.Equal(expected.TrinketOdds.ToArray(), context.Odds.TrinketOdds.ToArray());
            Assert.Equal(item ? 4 : 5, context.Odds.BatomonSlotsNextShop);
        }
    }

    [Fact]
    public void Moving_then_buying_uses_the_projected_slot_and_preserves_exact_labels()
    {
        var o = Observation(); o = o with { Board = o.Board! with { Team = o.Board.Team.SetItem(0, Creature(0, "a")) } };
        var actions = new LegalActionGenerator().Generate(o, Catalog);
        var move = actions.Single(a => a.Kind == ActionKind.Move && a.Source == new SlotAddress(SlotArea.Team, 0) && a.Target == new SlotAddress(SlotArea.Team, 1));
        var buy = actions.Single(a => a.Kind == ActionKind.BuyCreature && a.Source!.Index == 1 && a.Target == new SlotAddress(SlotArea.Team, 2));
        Assert.True(ShoppingSequenceValidator.CanFollow(o, Catalog, move, buy));
        var collision = actions.Single(a => a.Kind == ActionKind.BuyCreature && a.Source!.Index == 1 && a.Target == move.Target);
        Assert.False(ShoppingSequenceValidator.CanFollow(o, Catalog, move, collision));
    }

    [Fact]
    public void Positioning_uses_printed_targets_and_discards_relocations_with_unchanged_recipients()
    {
        var positional = Catalog.Monsters[0] with { Id = "trigger", Name = "Trigger", Rules = Rules with
            { MergeGroup = "trigger", Abilities = ImmutableDictionary<int, string>.Empty.Add(1, "Trigger the Bug ally above.") } };
        var catalog = Catalog with { Monsters = Catalog.Monsters.Add(positional) };
        var board = Observation().Board! with { Team = [Creature(0, "a"), Creature(1, "b"), Empty(2), Empty(3), Creature(4, "trigger"), Empty(5)] };
        var row = Assert.Single(ShoppingPositionContext.Describe(board, catalog));
        Assert.Equal("Beta", Assert.Single(row.Targets["above"]).Name);
        Assert.Equal("Team 2", row.Targets["above"][0].Slot);
        var swap = new GameAction("swap", ActionKind.Swap, "Swap", new(SlotArea.Team, 0), new(SlotArea.Team, 1));
        Assert.True(ShoppingPositionContext.ChangesPrintedTargets(board, catalog, swap));
        Assert.Equal("Alpha", ShoppingPositionContext.Describe(ShoppingPositionContext.Reposition(board, swap)!, catalog)[0].Targets["above"][0].Name);
        var ineffective = swap with { Kind = ActionKind.Move, Target = new(SlotArea.Team, 5) };
        Assert.False(ShoppingPositionContext.ChangesPrintedTargets(board, catalog, ineffective));
        var adjacent = catalog with { Monsters = catalog.Monsters.SetItem(3, positional with { Rules = positional.Rules! with
            { Abilities = ImmutableDictionary<int, string>.Empty.Add(1, "Trigger adjacent allies.") } }) };
        Assert.DoesNotContain(ShoppingPositionContext.Describe(board, adjacent)[0].Targets["adjacent"], t => t.DefinitionId == "a");
        Assert.Contains(ShoppingPositionContext.Describe(board, adjacent, true)[0].Targets["adjacent"], t => t.DefinitionId == "a");
    }

    [Fact]
    public void Next_payload_reassembles_chunks_and_preserves_variant_triggers_and_escaped_strings()
    {
        var payload = JsonSerializer.Serialize(new { monster = new { id = "a", types = new[] { "bug" }, keywords = new[] { "multicast" },
            ability = new { trigger = "On Cast", byLevel = new Dictionary<string, string> { ["1"] = "Trigger the ally \"above\"." } },
            shinyAbility = new { trigger = "On Battle Start", byLevel = new Dictionary<string, string> { ["1"] = "Trigger this." } },
            levels = new[] { new { level = 1, cooldown = 3.5m, multicast = 2 } } } });
        var chunks = new[] { payload[..20], payload[20..80], payload[80..] };
        var html = string.Concat(chunks.Select(chunk => "<script>self.__next_f.push(" + JsonSerializer.Serialize(new object[] { 1, chunk }) + ")</script>"));
        Assert.Equal(payload, WikiPayload.Decode(html));
        var rules = WikiPayload.MonsterRules("a", html)!;
        Assert.Equal("On Cast", rules.AbilityTrigger); Assert.Equal("On Battle Start", rules.ShinyAbilityTrigger);
        Assert.Equal("Trigger the ally \"above\".", rules.Abilities[1]);
        Assert.Equal(2, Assert.Single(rules.Levels).Multicast);
        Assert.Equal(rules.Abilities[1], WikiPayload.MonsterRules("a", payload)!.Abilities[1]);
    }

    [Fact]
    public void Mechanics_keep_non_sentence_rules_formulas_and_resolution_order()
    {
        const string html = "<main><h2>Exact rules</h2><ul><li>Multicast is total casts</li><li>Repeats stop on knockout.</li></ul><h2>Formula</h2><p>max(0.1s, base / (1 + speed) + added)</p><h2>Resolution</h2><p>Shield before Damage</p><h2>Current build examples</h2><p>Discard this example</p></main>";
        var rules = WikiPayload.MechanicsRules(html);
        Assert.Contains("Multicast is total casts", rules); Assert.Contains("max(0.1s", rules); Assert.Contains("Shield before Damage", rules);
        Assert.DoesNotContain("Discard", rules);
        Assert.Contains("max(0.1s", ShoppingPromptContext.LegacyRules("Exact rules\nTotal casts\nFormula\nmax(0.1s, base)\nCurrent build examples\nDiscard"));
    }

    [Fact]
    public void Pair_rows_preserve_exact_rates_and_count_denominators()
    {
        const string html = "<main><table><tbody><tr><th><img data-monster-card=\"a\"/><img data-monster-card=\"b\"/></th><td><meter value=\"0.06\"></meter><small>12 of 200 observations</small></td><td><meter value=\"0.5\"></meter><small>6 of 12 observations</small></td></tr></tbody></table></main>";
        var pair = Assert.Single(WikiPayload.PairRows(html, Catalog, "ranked · 30 days · Balance 24"));
        Assert.Equal(12, pair.SeenCount); Assert.Equal(200, pair.TotalCount); Assert.Equal(6, pair.WinCount);
        Assert.Equal(.5m, pair.RoundWinAssociation); Assert.Equal("a", pair.FirstId); Assert.Equal("b", pair.SecondId);
        Assert.Throws<InvalidDataException>(() => WikiPayload.PairRows(html.Replace("6 of 12", "missing"), Catalog, "window"));
        Assert.Equal("Dates 2026-09-08 to 2026-10-07 · mode ranked · Balance 24", StrategyKnowledgeImporter.ObservationWindow(
            "Mode Ranked Time window 30 days Apply filters Dates 2026-09-08 to 2026-10-07 · mode ranked · Balance 24\nObserved boards"));
    }

    [Fact]
    public void Composition_payload_keeps_hidden_phases_substitutes_and_supporting_items()
    {
        var payload = JsonSerializer.Serialize(new { qualifiedRows = new[] { new { id = "build", name = "Alpha core", coreUnitIds = new[] { "a" },
            details = new { phases = new[] { new { id = "early", sampleSize = 12, units = new[] { new { id = "a", medianLevel = 1 } } } },
                units = new[] { new { id = "a", substituteIds = new[] { "b" } } }, itemIdeas = new[] { new { id = "black_sludge", reason = "Supports Poison" } } } } } });
        var comp = Assert.Single(WikiPayload.CompositionRows(payload, Catalog, "ranked · 30 days"));
        Assert.Equal(12, comp.Data.GetProperty("details").GetProperty("phases")[0].GetProperty("sampleSize").GetInt32());
        Assert.Contains("b", ShoppingPromptContext.EntityIds(comp.Data)); Assert.Contains("black-sludge", ShoppingPromptContext.EntityIds(comp.Data));
    }

    [Fact]
    public async Task Bundled_prompt_serves_all_mechanics_and_preserves_displayed_probabilities()
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures/batomon-wiki.db"), true).GetLatestAsync())!;
        var knowledge = JsonSerializer.Deserialize<StrategyKnowledge>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets/wiki/strategy-knowledge.json")))!;
        Assert.Equal(31, knowledge.Mechanics.Length); Assert.Equal(100, knowledge.Pairs.Length); Assert.NotEmpty(knowledge.Compositions);
        Assert.True(CreatureNames.Matches(catalog.Monsters.Single(m => m.Id == "galvanade") with { Name = "Galvanade" }, "Electranade"));
        Assert.All(knowledge.Pairs, pair => { Assert.True(pair.SeenCount > 0); Assert.True(pair.TotalCount >= pair.SeenCount);
            Assert.InRange(pair.WinCount, 0, pair.SeenCount); Assert.NotEmpty(pair.ObservationWindow); });
        Assert.Contains("base ÷", knowledge.Mechanics.Single(m => m.Id == "mechanics/cooldown-speed").Rules);
        var board = Observation().Board! with { TrainerId = "musician", Team = Enumerable.Range(0, 6).Select(Empty).ToImmutableArray(),
            Shop = [Creature(0, "velocect") with { Price = 15, Affordable = true }, Creature(1, "galvanade") with { Price = 25, Affordable = true }, Empty(2), Empty(3), Empty(4)] };
        var displayed = board.ToShopOddsSnapshot(catalog) with { RegionFilter = MonsterRegionFilter.Jinto, ShopRank = 4, RarityOdds = ShopRankOdds.ForRank(4) };
        var context = ShoppingAdviceContext.Create(catalog, displayed, board, [], ["link-cable"]);
        var observation = Observation() with { Board = board, InventoryResolved = false, AdviceContext = context };
        var prompt = new ShoppingPromptContext(new(observation, catalog, knowledge, new LegalActionGenerator().Generate(observation, catalog), context.Odds, true, null, []) { AdviceContext = context });
        using var document = JsonDocument.Parse(prompt.Serialize());
        Assert.Equal(31, document.RootElement.GetProperty("mechanics").GetArrayLength());
        Assert.Contains("ANY type", document.RootElement.GetProperty("strategyRules").GetString());
        Assert.Contains("self-knockout", document.RootElement.GetProperty("strategyRules").GetString());
        Assert.Contains("galvanade", prompt.ServedIds); Assert.Contains("link-cable", prompt.ServedIds);
        Assert.NotEmpty(document.RootElement.GetProperty("pairEvidence").EnumerateArray());
        Assert.NotEmpty(document.RootElement.GetProperty("buildEvidence").EnumerateArray());
        var groups = document.RootElement.GetProperty("probabilities").GetProperty("creatures").EnumerateArray().ToArray();
        foreach (var odds in context.Odds.MonsterOdds)
        {
            var group = groups.Single(g => g.GetProperty("ids").EnumerateArray().Any(id => id.GetString() == odds.MonsterId));
            Assert.Equal(Math.Round(odds.NextShopChance, 6), group.GetProperty("nextShop").GetDecimal());
        }
        Assert.True(prompt.TrimOptional());
        using var compact = JsonDocument.Parse(prompt.Serialize());
        Assert.Equal(31, compact.RootElement.GetProperty("mechanics").GetArrayLength());
        Assert.NotEmpty(compact.RootElement.GetProperty("buildEvidence").EnumerateArray());
    }

    [Fact]
    public async Task Model_returns_two_valid_moves_and_rejects_unserved_citations()
    {
        var o = Observation(); var actions = new LegalActionGenerator().Generate(o, Catalog);
        var first = Array.FindIndex(actions.ToArray(), a => a.Kind == ActionKind.BuyCreature && a.Source!.Index == 0 && a.Target == new SlotAddress(SlotArea.Team, 0));
        var second = Array.FindIndex(actions.ToArray(), a => a.Kind == ActionKind.BuyCreature && a.Source!.Index == 1 && a.Target == new SlotAddress(SlotArea.Team, 1));
        var reply = JsonSerializer.Serialize(new { candidateId = $"c{first:D3}", reason = "Alpha adds repeated direct hits.", secondCandidateId = $"c{second:D3}",
            secondReason = "Beta fills another team slot.", secondReferences = new[] { "b" }, objective = "Build the battle team.", nextStep = "Look for affordable upgrades; stop if rolls are poor value.", references = new[] { "a" }, lookups = Array.Empty<string>() });
        using var http = new HttpClient(new Handler(reply));
        var decision = await new OllamaShoppingPlanner(http, new()).ProposeAsync(new(o, Catalog, Knowledge, actions, null, true, null, []));
        Assert.Equal(actions[second], decision.SecondMove!.Action);
        using var invalid = new HttpClient(new Handler(reply.Replace("\"references\":[\"a\"]", "\"references\":[\"evolved\"]")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new OllamaShoppingPlanner(invalid, new()).ProposeAsync(new(o, Catalog, Knowledge, actions, null, true, null, [])));
        Assert.Contains("not supplied", error.Message);
    }

    [Fact]
    public async Task Grounding_review_keeps_complete_rules_and_supplies_the_selected_action_facts()
    {
        var observation = Observation();
        var knowledge = Knowledge with { Version = 2, Mechanics = Enumerable.Range(0, 31)
            .Select(i => new MechanicEvidence("mechanics/m" + i, "Rule", "https://batomon.com/mechanics/m" + i, "Complete rule and formula " + i)).ToImmutableArray() };
        var handler = new Handler("{\"candidateId\":\"c000\",\"reason\":\"Alpha adds direct attacks.\",\"objective\":\"Improve survival.\",\"nextStep\":\"Seek useful upgrades; stop if none are affordable.\",\"references\":[\"a\"],\"lookups\":[]}");
        using var http = new HttpClient(handler);
        await new OllamaShoppingPlanner(http, new()).ProposeAsync(new(observation, Catalog, knowledge,
            new LegalActionGenerator().Generate(observation, Catalog), null, true, null, []));
        Assert.Equal(2, handler.Requests.Count);
        using var finalRequest = JsonDocument.Parse(handler.Requests.Last());
        var messages = finalRequest.RootElement.GetProperty("messages");
        using var preserved = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(31, preserved.RootElement.GetProperty("mechanics").GetArrayLength());
        Assert.Contains("Buy Alpha into Team 1", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.Contains("Do not treat the proposal as a performed move", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Explicit_contradictions_of_supplied_shock_rules_are_rejected()
    {
        var knowledge = Knowledge with { References = Knowledge.References.Add(new("mechanics/shock", "Shock", "https://batomon.com/mechanics/shock",
            "Exact rules\nShock reaches Shield before direct Damage.\nCurrent build examples\nExample.")) };
        var reply = "{\"candidateId\":\"c000\",\"reason\":\"Shock bypasses Shield.\",\"objective\":\"Improve survival.\",\"nextStep\":\"Stop if no upgrades are affordable.\",\"references\":[],\"lookups\":[]}";
        var handler = new Handler(reply); using var http = new HttpClient(handler); var observation = Observation();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new OllamaShoppingPlanner(http, new()).ProposeAsync(new(observation, Catalog, knowledge,
            new LegalActionGenerator().Generate(observation, Catalog), null, true, null, [])));
        Assert.Contains("does not bypass Shield", error.Message); Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Model_can_buy_into_a_slot_freed_by_the_first_move()
    {
        var catalog = Catalog with { Monsters = Catalog.Monsters.SetItem(0, Catalog.Monsters[0] with { Rules = Rules with
            { Abilities = ImmutableDictionary<int, string>.Empty.Add(1, "Charge the ally behind.") } }) };
        var o = Observation(); o = o with { InventoryResolved = false, Board = o.Board! with
            { Team = Enumerable.Range(0, 6).Select(i => Creature(i, i == 0 ? "a" : "evolved")).ToImmutableArray() } };
        var actions = new LegalActionGenerator().Generate(o, catalog);
        Assert.DoesNotContain(actions, action => action.Kind == ActionKind.BuyCreature && action.Target == new SlotAddress(SlotArea.Team, 0));
        using var http = new HttpClient(new CallbackHandler(body =>
        {
            using var request = JsonDocument.Parse(body);
            using var prompt = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var supplied = prompt.RootElement.GetProperty("legalActions").EnumerateArray().ToArray();
            var first = supplied.Single(row => row[1].GetString() == "Move Alpha to Bench 1")[0].GetString();
            var second = supplied.Single(row => row[1].GetString() == "Buy Beta into Team 1")[0].GetString();
            var primary = request.RootElement.GetProperty("format").GetProperty("properties").GetProperty("candidateId").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray();
            Assert.Contains(first, primary); Assert.DoesNotContain(second, primary);
            return JsonSerializer.Serialize(new { candidateId = first, reason = "Store Alpha to open a team position.", secondCandidateId = second,
                secondReason = "Deploy Beta into the freed team slot.", secondReferences = Array.Empty<string>(), objective = "Improve the active team.",
                nextStep = "Recapture after the purchase; stop if further upgrades are poor value.", references = Array.Empty<string>(), lookups = Array.Empty<string>() });
        }));
        var decision = await new OllamaShoppingPlanner(http, new()).ProposeAsync(new(o, catalog, Knowledge, actions, null, true, null, []));
        Assert.Equal(new SlotAddress(SlotArea.Team, 0), decision.SecondMove!.Action.Target);
        Assert.True(ShoppingSequenceValidator.CanFollow(o, catalog, decision.Action, decision.SecondMove.Action));
    }

    private sealed class CallbackHandler(Func<string, string> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { message = new { content = reply(await request.Content!.ReadAsStringAsync(cancellationToken)) }, done_reason = "stop", prompt_eval_count = 200 })) };
    }

    private sealed class Handler(string reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { message = new { content = reply }, done_reason = "stop", prompt_eval_count = 200 })) };
        }
    }
}
