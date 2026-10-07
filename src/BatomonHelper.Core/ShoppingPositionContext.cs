using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BatomonHelper.Core;

/// <summary>Printed directional targets on the captured 3x2 board, without simulating casts or combat.</summary>
public static class ShoppingPositionContext
{
    private static readonly string[] Directions = ["above", "below", "front", "behind", "left", "right", "adjacent", "row", "column", "opposite"];
    public sealed record Target(string Slot, string Name, string? DefinitionId)
    {
        [JsonIgnore] public string Signature { get; init; } = "";
    }
    public sealed record Ability(string Slot, string Name, string? Trigger, string PrintedRule, IReadOnlyDictionary<string, Target[]> Targets)
    {
        [JsonIgnore] public string ActorSignature { get; init; } = "";
    }

    public static Ability[] Describe(BoardSnapshot board, CatalogSnapshot catalog, bool linkCable = false)
    {
        return board.Team.Where(s => s.Occupancy == SlotOccupancy.Creature).SelectMany(slot =>
        {
            var monster = catalog.Monsters.FirstOrDefault(m => m.Id == slot.DefinitionId);
            if (monster?.Rules is not { } rules) return [];
            var level = slot.Level ?? 1;
            var variants = slot.Variant == ShinyState.Unknown ? new[] { false, true } : [slot.Variant == ShinyState.Shiny];
            return variants.Select(shiny => (Trigger: shiny ? rules.ShinyAbilityTrigger : rules.AbilityTrigger,
                Text: (shiny ? rules.ShinyAbilities : rules.Abilities).GetValueOrDefault(level)))
                .Where(ability => ability.Text is not null && Directions.Any(direction => Word(ability.Text, direction)))
                .Select(ability => new Ability($"Team {slot.Index + 1}", monster.Name, ability.Trigger, ability.Text!,
                    Directions.Where(direction => Word(ability.Text!, direction)).ToDictionary(direction => direction, direction =>
                        Indices(slot.Index, direction, linkCable).Select(index => board.Team[index]).Select(recipient =>
                            new Target($"Team {recipient.Index + 1}", recipient.Occupancy == SlotOccupancy.Empty ? "Empty" :
                                catalog.Monsters.FirstOrDefault(m => m.Id == recipient.DefinitionId)?.Name ?? "Unknown", recipient.DefinitionId)
                            { Signature = Fingerprint(recipient) }).ToArray())) { ActorSignature = Fingerprint(slot) });
        }).ToArray();
    }

    public static BoardSnapshot? Reposition(BoardSnapshot board, GameAction action)
    {
        if (action.Kind is not (ActionKind.Move or ActionKind.Swap) || action.Source is not { } source || action.Target is not { } target ||
            source.Area is not (SlotArea.Team or SlotArea.Bench) || target.Area is not (SlotArea.Team or SlotArea.Bench)) return null;
        var first = (source.Area == SlotArea.Team ? board.Team : board.Bench)[source.Index];
        var second = (target.Area == SlotArea.Team ? board.Team : board.Bench)[target.Index];
        board = source.Area == SlotArea.Team ? board with { Team = board.Team.SetItem(source.Index, second with { Index = source.Index }) }
            : board with { Bench = board.Bench.SetItem(source.Index, second with { Index = source.Index }) };
        return target.Area == SlotArea.Team ? board with { Team = board.Team.SetItem(target.Index, first with { Index = target.Index }) }
            : board with { Bench = board.Bench.SetItem(target.Index, first with { Index = target.Index }) };
    }

    public static bool ChangesPrintedTargets(BoardSnapshot board, CatalogSnapshot catalog, GameAction action, bool linkCable = false)
    {
        if (Reposition(board, action) is not { } after) return false;
        var beforeRows = Describe(board, catalog, linkCable); var afterRows = Describe(after, catalog, linkCable);
        // Enemy slots are not captured. A printed enemy/row/opposite interaction may still change with position.
        if (beforeRows.Concat(afterRows).Any(row => row.Targets.ContainsKey("opposite") || Word(row.PrintedRule, "enemy") || Word(row.PrintedRule, "enemies"))) return true;
        return !Signatures(beforeRows).SequenceEqual(Signatures(afterRows));
    }

    private static string[] Signatures(Ability[] rows) => rows.Select(row => row.ActorSignature + "|" + row.PrintedRule + "|" +
        string.Join('|', row.Targets.Select(pair => pair.Key + ":" + string.Join(';', pair.Value.Select(t => t.Signature).Order(StringComparer.Ordinal)))))
        .Order(StringComparer.Ordinal).ToArray();
    private static string Fingerprint(BoardSlot slot) => slot.Occupancy == SlotOccupancy.Empty ? "empty" :
        JsonSerializer.Serialize(slot with { Index = 0, Price = null, Affordable = null });
    private static bool Word(string text, string word) => Regex.IsMatch(text, @"\b" + word + @"\b", RegexOptions.IgnoreCase);
    private static IEnumerable<int> Indices(int index, string direction, bool linkCable)
    {
        return direction switch
        {
            "above" or "front" => index >= 3 ? [index - 3] : [],
            "below" or "behind" => index < 3 ? [index + 3] : [],
            "left" => index % 3 > 0 ? [index - 1] : [],
            "right" => index % 3 < 2 ? [index + 1] : [],
            "adjacent" when linkCable => Enumerable.Range(0, 6).Where(i => i != index),
            "adjacent" => Enumerable.Range(0, 6).Where(i => i / 3 == index / 3 && Math.Abs(i - index) == 1 || Math.Abs(i - index) == 3),
            "row" => Enumerable.Range(index / 3 * 3, 3),
            "column" => [index % 3, index % 3 + 3],
            _ => [] // Opposite is on the unobserved enemy board.
        };
    }
}
