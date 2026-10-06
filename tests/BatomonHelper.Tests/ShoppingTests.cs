using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BatomonHelper.Tests;

public sealed class ShoppingTests
{
    private static readonly CatalogSnapshot Catalog = CatalogSnapshot.Empty with { DeclaredSteamBuild = "test", Monsters = [new("a", "Alpha", Rarity.Common, null, ["starter"]), new("b", "Beta", Rarity.Common, null, ["starter"])], Trainers = [new("trainer", "Trainer")], Items = [new("item", "Item", "Select a monster.")], Trinkets = [new("gift", "Gift", Rarity.Common, "Gain 5 HP.")] };
    private static readonly StrategyKnowledge Knowledge = new(1, "test", "24", null, DateTimeOffset.UtcNow, [new("mechanics/monster-pool", "Shop", "https://batomon.com", "Shop rules.")]);
    private static BoardSlot Empty(int i) => new(i, SlotOccupancy.Empty, Stats: []);
    private static BoardSlot Creature(int i, string id = "a", int level = 1) => new(i, SlotOccupancy.Creature, id, ShinyState.Normal, level, [new(VisibleStatKind.Damage, 5)]);
    private static ShoppingObservation Observation() => new()
    {
        Board = new() { TrainerId = "trainer", Day = 1, Lives = 10, Wins = 0, WinTarget = 10, TeamHp = 300, Gold = 30, ShopRank = 1, RarityOdds = Enum.GetValues<Rarity>().Where(r => r != Rarity.Mythical).Select(r => new CapturedRarityOdds(r, r == Rarity.Common ? 1 : 0)).ToImmutableArray(), FreeRerolls = 1, ShopLocked = false,
            Team = Enumerable.Range(0, 6).Select(Empty).ToImmutableArray(), Bench = Enumerable.Range(0, 4).Select(Empty).ToImmutableArray(),
            Shop = [Creature(0) with { Price = 10, Affordable = true }, Empty(1), Empty(2), Empty(3), Empty(4)] },
        Panel = ShoppingPanel.Shop, Resolved = true, Foreground = true,
        Geometry = new(1, 1, 0, 0, 1920, 1080, 0, 0, 1920, 1080, 96)
    };
    [Fact]
    public void Legal_actions_reject_unaffordable_unknown_level_and_full_placement()
    {
        var o = Observation(); var legal = new LegalActionGenerator();
        Assert.Equal(10, legal.Generate(o, Catalog).Count(a => a.Kind == ActionKind.BuyCreature));
        var expensive = o.Board!.Shop.SetItem(0, o.Board.Shop[0] with { Price = 31, Affordable = false });
        Assert.DoesNotContain(legal.Generate(o with { Board = o.Board with { Shop = expensive } }, Catalog), a => a.Kind == ActionKind.BuyCreature);
        Assert.DoesNotContain(legal.Generate(o with { Board = o.Board with { Shop = o.Board.Shop.SetItem(0, o.Board.Shop[0] with { Level = null }) } }, Catalog), a => a.Kind == ActionKind.BuyCreature);
        Assert.DoesNotContain(legal.Generate(o with { Board = o.Board with { Team = Enumerable.Range(0,6).Select(i => Creature(i)).ToImmutableArray(), Bench = Enumerable.Range(0,4).Select(i => Creature(i)).ToImmutableArray() } }, Catalog), a => a.Kind == ActionKind.BuyCreature);
    }
    [Fact]
    public void Unknown_sales_merges_item_uses_and_invalid_targets_are_blocked()
    {
        var o = Observation(); o = o with { Board = o.Board! with { Team = o.Board.Team.SetItem(0, Creature(0)).SetItem(1, Creature(1)) } };
        var legal = new LegalActionGenerator();
        Assert.DoesNotContain(legal.Generate(o, Catalog), a => a.Kind is ActionKind.Sell or ActionKind.Merge or ActionKind.UseItem);
        o = o with { Details = [new(new(SlotArea.Team,0), [], 1, 5, null, true)] };
        Assert.Contains(legal.Generate(o, Catalog), a => a.Kind == ActionKind.Sell && a.Cost == -5);
        Assert.DoesNotContain(legal.Generate(o with { Panel = ShoppingPanel.TargetSelection, TargetsResolved = true, PendingDefinitionId = "item", EnabledTargets = [new(SlotArea.Team, 5), new(SlotArea.Shop,0), new(SlotArea.Team,99)] }, Catalog), a => a.Kind == ActionKind.UseItem);
        Assert.Empty(legal.Generate(o with { Resolved = false }, Catalog));
        Assert.Empty(legal.Generate(o with { Panel = ShoppingPanel.Battle }, Catalog));
    }
    [Fact]
    public void Revision_ignores_capture_time_and_catalog_filters_but_tracks_geometry_inventory_and_price()
    {
        var o = Observation();
        Assert.Equal(o.Revision, (o with { ObservedAt = DateTimeOffset.MaxValue, Board = o.Board! with { CapturedAt = DateTimeOffset.UtcNow, RegionFilter = MonsterRegionFilter.Jinto } }).Revision);
        Assert.NotEqual(o.Revision, (o with { Geometry = o.Geometry! with { ClientX = -1000 } }).Revision);
        Assert.NotEqual(o.Revision, (o with { Inventory = [new(0,"item",1,false,true)] }).Revision);
        Assert.NotEqual(o.Revision, (o with { Board = o.Board! with { Gold = 20 } }).Revision);
    }
    [Fact]
    public async Task Advice_uses_the_supplied_capture_without_live_focus_or_geometry()
    {
        var captured = Observation() with { Foreground = false, Geometry = null };
        var planner = new Planner(); var journal = new Journal();
        var session = new ShoppingAdviceSession(new LegalActionGenerator(),planner,journal,Catalog,Knowledge);
        await session.SuggestAsync(captured);
        Assert.Equal(ShoppingStatus.Suggested,session.State.Status);
        Assert.Same(captured,planner.LastRequest!.Observation);
        Assert.Equal("Next step",session.State.Decision!.NextStep);
        Assert.Single(journal.Entries,e => e.Kind == "advice");
        await session.SuggestAsync(captured);
        Assert.Equal("Objective",planner.LastRequest.Objective);
        Assert.Empty(planner.LastRequest.RecentActions);
    }
    [Fact]
    public async Task Repeated_requests_are_ignored_and_new_capture_cancels_old_advice()
    {
        var planner = new Planner { Hold = true };
        var session = new ShoppingAdviceSession(new LegalActionGenerator(),planner,new Journal(),Catalog,Knowledge);
        var pending = session.SuggestAsync(Observation()); await planner.Entered.Task;
        await session.SuggestAsync(Observation()); Assert.Equal(1,planner.Calls);
        session.Cancel("New capture"); planner.Release.SetResult(); await pending;
        Assert.Equal(ShoppingStatus.Cancelled,session.State.Status); Assert.Null(session.State.Decision);
    }
    [Fact]
    public async Task Invalid_capture_and_invented_advice_are_rejected()
    {
        var planner = new Planner();
        var session = new ShoppingAdviceSession(new LegalActionGenerator(),planner,new Journal(),Catalog,Knowledge);
        await session.SuggestAsync(Observation() with { Resolved = false }); Assert.Equal(0,planner.Calls);
        planner.Invent = true;
        await session.SuggestAsync(Observation()); Assert.Null(session.State.Decision);
        Assert.Equal(ShoppingStatus.Idle,session.State.Status);
    }
    [Fact]
    public async Task Model_returns_one_move_with_a_conditional_follow_up()
    {
        var handler = new Handler(_ => "{\"candidateId\":\"c000\",\"reason\":\"Adds immediate strength.\",\"objective\":\"Build a stronger front row.\",\"nextStep\":\"Look for matching upgrades; recapture before rerolling.\",\"references\":[],\"lookups\":[]}");
        using var http = new HttpClient(handler); var o = Observation();
        var result = await new OllamaShoppingPlanner(http,new()).ProposeAsync(new(o,Catalog,Knowledge,new LegalActionGenerator().Generate(o,Catalog),null,true,null,[]));
        Assert.Equal("Look for matching upgrades; recapture before rerolling.",result.NextStep);
        Assert.Equal(ActionKind.BuyCreature,result.Action.Kind);
        Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public async Task Thinking_gets_room_for_final_json_and_length_repair_requests_a_bounded_answer()
    {
        var reply = "{\"candidateId\":\"c000\",\"reason\":\"Strength.\",\"objective\":\"Survive.\",\"nextStep\":\"Recapture.\",\"references\":[],\"lookups\":[]}";
        var handler = new Handler(_ => reply)
        {
            Envelope = call => new { message = new { content = call == 1 ? "" : reply, thinking = "private reasoning" },
                done_reason = call == 1 ? "length" : "stop", eval_count = call == 1 ? 4096 : 300, prompt_eval_count = 200 }
        };
        using var http = new HttpClient(handler); var o = Observation(); var journal = new Journal();
        var decision = await new OllamaShoppingPlanner(http,new() { Thinking = true },journal)
            .ProposeAsync(new(o,Catalog,Knowledge,new LegalActionGenerator().Generate(o,Catalog),null,true,null,[]));
        Assert.Equal("Recapture.",decision.NextStep);
        Assert.Equal(4096,handler.Requests[0].GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(2048,handler.Requests[1].GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(handler.Requests[1].GetProperty("think").GetBoolean());
        var citationIds = handler.Requests[0].GetProperty("format").GetProperty("properties").GetProperty("references").GetProperty("items").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Contains("a",citationIds);
        Assert.DoesNotContain("creatures/a",citationIds);
        Assert.Contains(handler.Requests[1].GetProperty("messages").EnumerateArray(),m => m.GetProperty("role").GetString() == "assistant");
        Assert.Contains(journal.Entries,e => e.Kind == "model-validation-error" && e.Message!.Contains("4096-token"));
        Assert.Equal(2,journal.Entries.Count(e => e.Kind == "model-response"));
        Assert.DoesNotContain(journal.Entries,e => e.Message?.Contains("private reasoning") == true);
    }
    [Fact]
    public async Task Repeated_truncation_reports_the_actual_error_and_stops_after_one_repair()
    {
        var handler = new Handler(_ => "") { Envelope = _ => new { message = new { content = "" }, done_reason = "length", prompt_eval_count = 200 } };
        using var http = new HttpClient(handler); var o = Observation();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new OllamaShoppingPlanner(http,new() { Thinking = true })
            .ProposeAsync(new(o,Catalog,Knowledge,new LegalActionGenerator().Generate(o,Catalog),null,true,null,[])));
        Assert.Contains("2048-token output limit",error.Message);
        Assert.Equal(2,handler.Calls);
    }
    [Theory]
    [InlineData("not json")] [InlineData("{\"candidateId\":\"invented\",\"reason\":\"x\",\"objective\":\"x\",\"references\":[],\"lookups\":[]}")]
    [InlineData("{\"candidateId\":null,\"reason\":false,\"lookups\":{}}")]
    public async Task Malformed_or_invented_model_output_gets_one_repair_then_fails(string response)
    {
        var handler = new Handler(_ => response); using var http = new HttpClient(handler);
        var o = Observation(); var planner = new OllamaShoppingPlanner(http,new());
        await Assert.ThrowsAsync<InvalidDataException>(() => planner.ProposeAsync(new(o,Catalog,Knowledge,new LegalActionGenerator().Generate(o,Catalog),null,true,null,[])));
        Assert.Equal(2,handler.Calls);
    }
    [Fact]
    public async Task Model_cancellation_and_remote_endpoints_are_rejected()
    {
        var handler = new Handler(_ => "{}") { Hold = true }; using var http = new HttpClient(handler);
        var o = Observation(); using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OllamaShoppingPlanner(http,new()).ProposeAsync(new(o,Catalog,Knowledge,new LegalActionGenerator().Generate(o,Catalog),null,true,null,[]),cancel.Token));
        Assert.Equal(1,handler.Calls);
        Assert.Throws<InvalidDataException>(() => (new ShoppingSettings { Endpoint = "http://example.com" }).Validate());
    }
    private sealed class Planner : IShoppingPlanner
    {
        public int Calls; public bool Hold, Invent; public PlannerRequest? LastRequest;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ShoppingDecision> ProposeAsync(PlannerRequest request,CancellationToken token = default)
        {
            Calls++; LastRequest = request; Entered.TrySetResult();
            if (Hold) await Release.Task; // Deliberately ignore cancellation: session must discard a late response.
            return new(request.Observation.Revision,Invent ? new("invented",ActionKind.Reroll,"Invented") : request.Candidates.First(),"Reason","Objective",[]) { NextStep = "Next step" };
        }
    }
    private sealed class Journal : IShoppingJournal { public List<ShoppingJournalEntry> Entries = []; public Task AppendAsync(ShoppingJournalEntry e,CancellationToken token = default) { Entries.Add(e); return Task.CompletedTask; } }
    private sealed class Handler(Func<int,string> output) : HttpMessageHandler
    {
        public int Calls; public bool Hold;
        public Func<int,object>? Envelope;
        public List<JsonElement> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token)
        {
            var count = Interlocked.Increment(ref Calls);
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token)); Requests.Add(body.RootElement.Clone());
            if (Hold) await Task.Delay(Timeout.Infinite,token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Envelope?.Invoke(count) ?? new { message = new { content = output(count) }, prompt_eval_count = 200 })) };
        }
    }
}
