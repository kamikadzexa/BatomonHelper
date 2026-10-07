using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Core;

public sealed class ShoppingPromptContext
{
    public Dictionary<string, object?> Sections { get; } = new();
    public HashSet<string> ServedIds { get; } = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Serialize() => JsonSerializer.Serialize(Sections, JsonOptions);

    public ShoppingPromptContext(PlannerRequest request)
    {
        var observation = request.Observation; var board = observation.Board!;
        var catalog = request.Catalog; var knowledge = request.Knowledge;
        var context = request.AdviceContext ?? observation.AdviceContext;
        var odds = context?.Odds ?? request.Odds;
        var observedIds = board.Team.Concat(board.Bench).Concat(board.Shop).Select(s => s.DefinitionId)
            .Concat(observation.Inventory.Where(i => i.Resolved).Select(i => i.DefinitionId))
            .Concat(context?.OwnedUniqueTrinketIds ?? []).Where(id => id is not null).Select(id => id!).ToHashSet();
        var pairs = knowledge.Pairs.Where(p => observedIds.Contains(p.FirstId) || observedIds.Contains(p.SecondId))
            .OrderByDescending(p => observedIds.Contains(p.FirstId) && observedIds.Contains(p.SecondId))
            .ThenByDescending(p => p.SeenCount).Take(8).ToArray();
        var comps = knowledge.Compositions.Where(c => c.CoreIds.Any(observedIds.Contains) ||
            EntityIds(c.Data).Any(observedIds.Contains)).OrderByDescending(c => c.CoreIds.Count(observedIds.Contains))
            .ThenByDescending(c => Number(c.Data, "runs")).Take(3).ToArray();
        var relatedIds = pairs.SelectMany(p => new[] { p.FirstId, p.SecondId }).Concat(comps.SelectMany(c => c.CoreIds))
            .Concat(comps.SelectMany(c => EntityIds(c.Data))).Where(id => !observedIds.Contains(id))
            .Distinct().ToArray();
        var partnerIds = relatedIds.Where(id => catalog.Monsters.Any(m => m.Id == id))
            .OrderByDescending(id => odds?.MonsterOdds.FirstOrDefault(o => o.MonsterId == id)?.NextShopChance ?? 0).Take(6)
            .Concat(relatedIds.Where(id => catalog.Items.Any(i => i.Id == id) || catalog.Trinkets.Any(t => t.Id == id) ||
                catalog.Trainers.Any(t => t.Id == id)).Take(4)).ToHashSet();
        Sections["boardSlotColumns"] = new[] { "index (zero based)", "occupancy", "definitionId", "variant", "level", "stats [kind,value]", "price", "affordable" };
        Sections["state"] = new { board = new { board.TrainerId, board.Day, board.Lives, board.Wins, board.WinTarget, board.TeamHp, board.Gold,
            board.ShopRank, board.RarityOdds, board.RerollCost, board.FreeRerolls, board.ItemUsesRemaining, board.ItemUsesMaximum,
            board.ItemUsesCounterVisible, board.ShopLocked, team = Slots(board.Team), bench = Slots(board.Bench), shop = Slots(board.Shop) },
            observation.Panel, observation.Inventory, observation.InventoryResolved,
            observation.Details, observation.Choices, observation.OfferLevels, observation.EnabledTargets,
            observation.TargetsResolved, observation.PendingDefinitionId, observation.PanelText };
        Sections["stateLegend"] = new { slotArea = Enum.GetValues<SlotArea>().Select(e => $"{(int)e}={e}"),
            occupancy = Enum.GetValues<SlotOccupancy>().Select(e => $"{(int)e}={e}"), statKind = Enum.GetValues<VisibleStatKind>().Select(e => $"{(int)e}={e}"),
            rarity = Enum.GetValues<Rarity>().Select(e => $"{(int)e}={e}"), shiny = Enum.GetValues<ShinyState>().Select(e => $"{(int)e}={e}") };
        Sections["goal"] = "Improve next-battle strength and survival, feasible scaling and economy. Protect low lives. Reassess the previous objective against this capture.";
        Sections["previousObjective"] = request.Objective;
        Sections["poolScenario"] = context is null ? null : new { context.Snapshot.ShopRank, region = context.Snapshot.RegionFilter.ToString(),
            context.Snapshot.ItemAvailable, maxedIds = context.LevelThreeMonsterIds.Order(), ownedTrinketIds = context.OwnedUniqueTrinketIds.Order(), context.Assumptions };
        Sections["probabilities"] = odds is null ? null : new { odds.RarityOdds, odds.BatomonSlotsNextShop, odds.Notes,
            units = "Probabilities are fractions, not percentages. Every ID in a group has the listed chance. Creatures: per Batomon slot and at least one in next shop. Items: per item slot / next shop. Trinkets: per reward roll, never shop offers.",
            creatures = odds.MonsterOdds.GroupBy(o => (o.Rarity, o.PerBatomonRoll, o.NextShopChance)).Select(g => new { rarity = g.Key.Rarity.ToString(),
                perSlot = Math.Round(g.Key.PerBatomonRoll, 6), nextShop = Math.Round(g.Key.NextShopChance, 6), ids = g.Select(o => o.MonsterId) }),
            items = odds.ItemOdds.GroupBy(o => (o.Rarity, o.PerItemRoll, o.NextShopChance)).Select(g => new { rarity = g.Key.Rarity.ToString(),
                perSlot = Math.Round(g.Key.PerItemRoll, 6), nextShop = Math.Round(g.Key.NextShopChance, 6), ids = g.Select(o => o.ItemId) }),
            trinkets = odds.TrinketOdds.GroupBy(o => (o.Rarity, o.PerTrinketRoll, o.EligibleCount, o.IsEligible)).Select(g => new { rarity = g.Key.Rarity.ToString(),
                perReward = Math.Round(g.Key.PerTrinketRoll, 6), g.Key.EligibleCount, g.Key.IsEligible, ids = g.Select(o => o.TrinketId) }) };
        Sections["probabilityNote"] = "Displayed manual scenarios affect probabilities only. Captured gold, prices, stats and item uses determine legal actions. Pools may have unknown run restrictions. Unavailable partners may be obtainable through evolution/events, not shop rolls; never invent their chance.";
        var trainer = catalog.Trainers.FirstOrDefault(t => t.Id == board.TrainerId);
        Sections["trainer"] = trainer;
        if (trainer is not null) ServedIds.Add(trainer.Id);
        Sections["creatures"] = catalog.Monsters.Where(m => observedIds.Contains(m.Id)).Select(m => Definition(m,
            board.Team.Concat(board.Bench).Concat(board.Shop).Where(s => s.DefinitionId == m.Id).Select(s => s.Level ?? 1).ToArray(),
            board.Team.Concat(board.Bench).Concat(board.Shop).Where(s => s.DefinitionId == m.Id).Select(s => s.Variant).Distinct().ToArray())).ToArray();
        Sections["items"] = catalog.Items.Where(i => observedIds.Contains(i.Id) || observation.Choices.Any(c => c.DefinitionId == i.Id)).ToArray();
        Sections["trinkets"] = catalog.Trinkets.Where(t => observedIds.Contains(t.Id) || observation.Choices.Any(c => c.DefinitionId == t.Id)).ToArray();
        foreach (var id in observedIds.Concat(observation.Choices.Select(c => c.DefinitionId).OfType<string>())) ServedIds.Add(id);
        var mechanics = knowledge.Mechanics.IsEmpty ? knowledge.References.Where(r => r.Id.StartsWith("mechanics/"))
            .Select(r => new MechanicEvidence(r.Id, r.Title, r.SourceUrl, LegacyRules(r.Text))).ToArray() : knowledge.Mechanics.ToArray();
        Sections["mechanics"] = mechanics.Select(m => new { m.Id, m.Rules }).ToArray();
        foreach (var mechanic in mechanics) ServedIds.Add(mechanic.Id);
        Sections["knowledgeVersion"] = new { knowledge.Version, wikiVersion = catalog.WikiVersion, knowledge.SteamBuild, knowledge.Balance, knowledge.PatchVersion, knowledge.RetrievedAt };
        Sections["catalogIndex"] = catalog.Monsters.Where(m => observedIds.Contains(m.Id) || partnerIds.Contains(m.Id) ||
            odds is null || odds.MonsterOdds.Any(o => o.MonsterId == m.Id && o.PerBatomonRoll > 0)).Select(m => new[] { m.Id, m.Name }).ToArray();
        Sections["referenceIndex"] = knowledge.References.Where(r => !r.Id.StartsWith("mechanics/")).Select(r => r.Id).ToArray();
        Sections["strategyRules"] = "Shock rewards frequent DIRECT DAMAGE hits from allies of ANY type, including non-Electric attackers and multicast. Status damage does not trigger Shock. Multicast is total casts; self-knockout stops repeats. Triggering uses current multicast without resetting cooldown progress. Apply exact target/type/exclusion rules and current 3x2 adjacency. Wiki composition positions are observational, not recommended positioning. Explain the actual ability interaction, not just a shared type. Distinguish permanent scaling from battle-only buffs; early survival can outweigh a late-game core. Pair round-win association is not a predicted win probability or causal synergy.";
        Sections["partnerAvailability"] = catalog.Monsters.Where(m => partnerIds.Contains(m.Id)).Select(m => new
        { m.Id, nextShop = odds?.MonsterOdds.FirstOrDefault(o => o.MonsterId == m.Id)?.NextShopChance ?? 0,
            meaning = "Zero means absent from the displayed shop pool; captured offers remain available." }).ToArray();
        SetOptional(pairs, comps, catalog, partnerIds);
        static object?[][] Slots(ImmutableArray<BoardSlot> slots) => slots.Select(s => new object?[] { s.Index, s.Occupancy.ToString(),
            s.DefinitionId, s.Variant.ToString(), s.Level, s.Stats.Select(stat => new object?[] { stat.Kind.ToString(), stat.Value }), s.Price, s.Affordable }).ToArray();
    }

    private readonly List<string> _optionalIds = [];
    private HashSet<string> _requiredIds = [];
    private PairEvidence[] _pairs = [];
    private CompositionEvidence[] _comps = [];
    private CatalogSnapshot _catalog = CatalogSnapshot.Empty;
    private HashSet<string> _partners = [];
    private int _trimStage;
    private void SetOptional(PairEvidence[] pairs, CompositionEvidence[] comps, CatalogSnapshot catalog, HashSet<string> partners)
    {
        _requiredIds = ServedIds.ToHashSet(StringComparer.Ordinal);
        _pairs = pairs; _comps = comps;
        _catalog = catalog; _partners = partners;
        Sections["pairEvidence"] = pairs;
        Sections["buildEvidence"] = comps.Select(CompositionSummary).ToArray();
        Sections["partnerCreatures"] = catalog.Monsters.Where(m => partners.Contains(m.Id)).Select(m => Definition(m, [1])).ToArray();
        Sections["supportingItems"] = catalog.Items.Where(i => partners.Contains(i.Id)).ToArray();
        Sections["supportingTrinkets"] = catalog.Trinkets.Where(t => partners.Contains(t.Id)).ToArray();
        Sections["supportingTrainers"] = catalog.Trainers.Where(t => partners.Contains(t.Id)).ToArray();
        _optionalIds.AddRange(pairs.Select(p => p.Id).Concat(comps.Select(c => c.Id)).Concat(partners));
        foreach (var id in _optionalIds) ServedIds.Add(id);
    }
    public bool TrimOptional()
    {
        if (_trimStage++ == 0)
        {
            // Keep complete statistics and a concise build path before removing optional evidence entirely.
            Sections.Remove("catalogIndex");
            Sections.Remove("referenceIndex");
            Sections["pairEvidence"] = _pairs.Take(3).ToArray();
            Sections["buildEvidence"] = _comps.Take(1).Select(c => new { c.Id, c.Name, c.CoreIds, c.SourceUrl, c.ObservationWindow,
                data = c.Data.EnumerateObject().Where(p => p.Name is "runs" or "players" or "rawFinish" or "averageFinalRound")
                    .ToDictionary(p => p.Name, p => p.Value.Clone()),
                phases = c.Data.GetProperty("details").GetProperty("phases").EnumerateArray().Select(phase => new
                { phase = phase.GetProperty("id").GetString(), sampleSize = phase.GetProperty("sampleSize").GetInt32(),
                    units = phase.GetProperty("units").EnumerateArray().Take(3).Select(unit => new
                    { id = unit.GetProperty("id").GetString(), level = unit.GetProperty("medianLevel").GetDecimal() }) }) }).ToArray();
            // Keep at least a small mechanically defined core and supporting effects after trimming meta records.
            Sections["partnerCreatures"] = _catalog.Monsters.Where(m => _partners.Contains(m.Id)).Take(2).Select(m => Definition(m, [1])).ToArray();
            foreach (var id in _optionalIds.Where(id => !_requiredIds.Contains(id))) ServedIds.Remove(id);
            foreach (var id in _pairs.Take(3).Select(p => p.Id).Concat(_comps.Take(1).Select(c => c.Id))) ServedIds.Add(id);
            foreach (var id in _catalog.Monsters.Where(m => _partners.Contains(m.Id)).Take(2).Select(m => m.Id)
                .Concat(_partners.Where(id => !_catalog.Monsters.Any(m => m.Id == id)))) ServedIds.Add(id);
            return true;
        }
        if (!Sections.Remove("buildEvidence")) return false;
        foreach (var key in new[] { "pairEvidence", "partnerCreatures", "supportingItems", "supportingTrinkets", "supportingTrainers" }) Sections.Remove(key);
        foreach (var id in _optionalIds.Where(id => !_requiredIds.Contains(id))) ServedIds.Remove(id);
        return true;
    }
    public static string LegacyRules(string text)
    {
        var start = text.IndexOf("Exact rules", StringComparison.Ordinal);
        if (start < 0) return text;
        var end = new[] { "Current build examples", "Common questions", "Related mechanics" }.Select(marker => text.IndexOf(marker, start, StringComparison.Ordinal))
            .Where(i => i >= 0).DefaultIfEmpty(text.Length).Min();
        return text[(start + "Exact rules".Length)..end].Trim();
    }
    public static object Definition(MonsterDefinition monster, int[]? observedLevels = null, ShinyState[]? variants = null)
    {
        var rules = monster.Rules;
        var levels = observedLevels is null ? null : observedLevels.SelectMany(l => new[] { l, Math.Min(4, l + 1) }).ToHashSet();
        var normal = variants is null || variants.Length == 0 || variants.Contains(ShinyState.Unknown) || variants.Contains(ShinyState.Normal);
        var shiny = variants is null || variants.Length == 0 || variants.Contains(ShinyState.Unknown) || variants.Contains(ShinyState.Shiny);
        return new { monster.Id, monster.Name, rarity = monster.Rarity.ToString(), monster.BaseSpeciesId,
            rules = rules is null ? null : new { rules.Types, rules.Keywords, rules.AbilityTrigger, rules.ShinyAbilityTrigger,
                levelColumns = new[] { "level", "cooldown", "multicast", "stats [kind,value]" },
                levels = normal ? LevelRows(rules.Levels) : null, shinyLevels = shiny ? LevelRows(rules.ShinyLevels) : null,
                abilities = normal ? rules.Abilities.Where(a => levels is null || levels.Contains(a.Key)).ToDictionary() : null,
                shinyAbilities = shiny ? rules.ShinyAbilities.Where(a => levels is null || levels.Contains(a.Key)).ToDictionary() : null,
                rules.Evolution, rules.MergeGroup, rules.UnverifiedPatchFields },
            referenceCost = monster.ReferenceValues.GetValueOrDefault("Cost"),
            referenceNotes = observedLevels is null ? monster.ReferenceNotes : [] };
        object?[][] LevelRows(ImmutableArray<MonsterLevelRules> entries) => entries.Where(l => levels is null || levels.Contains(l.Level)).Select(l =>
            new object?[] { l.Level, l.Cooldown, l.Multicast, l.Stats.Select(s => new object?[] { s.Kind.ToString(), s.Value }) }).ToArray();
    }
    public static object CompositionSummary(CompositionEvidence composition) => new { composition.Id, composition.Name, composition.CoreIds,
        composition.SourceUrl, composition.ObservationWindow, data = Compact(composition.Data) };
    private static object? Compact(JsonElement element)
    {
        var keys = new HashSet<string>(["id", "name", "tier", "coreUnitIds", "representativeUnits", "medianLevel", "presenceRate", "core", "flex", "substituteIds",
            "rawFinish", "numerator", "denominator", "rate", "averageCappedWins", "playRate", "runs", "players", "terminalRuns", "averageFinalRound",
            "details", "phases", "roundLabel", "sampleSize", "units", "nonterminalRoundWins", "trainers", "trinkets", "itemIdeas", "reason", "weakSpots", "effect", "phase", "winRate", "delta"]);
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Where(p => keys.Contains(p.Name)).ToDictionary(p => p.Name, p => Compact(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Take(6).Select(Compact).ToArray(),
            _ => element.Clone()
        };
    }
    private static decimal Number(JsonElement element, string key) => element.TryGetProperty(key, out var n) && n.TryGetDecimal(out var value) ? value : 0;
    public static IEnumerable<string> EntityIds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "id" && property.Value.ValueKind == JsonValueKind.String) yield return property.Value.GetString()!;
                else if (property.Name == "substituteIds" && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var id in property.Value.EnumerateArray()) yield return id.GetString()!;
                else foreach (var id in EntityIds(property.Value)) yield return id;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) foreach (var id in EntityIds(child)) yield return id;
    }
}
