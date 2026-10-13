using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatomonHelper.Core;

public sealed record MonsterLevelRules(int Level, decimal? Cooldown, int? Multicast, ImmutableArray<VisibleStat> Stats);
public sealed record MonsterRules(ImmutableArray<string> Types, ImmutableArray<string> Keywords,
    ImmutableArray<MonsterLevelRules> Levels, ImmutableArray<MonsterLevelRules> ShinyLevels,
    ImmutableDictionary<int, string> Abilities, ImmutableDictionary<int, string> ShinyAbilities,
    string? Evolution, string MergeGroup, string SourceUrl, DateTimeOffset RetrievedAt)
{
    public string? AbilityTrigger { get; init; }
    public string? ShinyAbilityTrigger { get; init; }
    public ImmutableArray<string> UnverifiedPatchFields { get; init; } = [];
}

public enum ShoppingPanel { Unknown, Shop, Backpack, TargetSelection, TrinketReward, Event, Battle, Result, RunEnd }
public enum ActionKind { BuyCreature, BuyItem, Sell, Move, Swap, Merge, UseItem, ChooseReward, ChooseEvent, Reroll, SetLock, Finish }
public enum SlotArea { Team, Bench, Shop, Inventory, Choice }
public sealed record SlotAddress(SlotArea Area, int Index);
public sealed record ObservedInventoryEntry(int Index, string DefinitionId, int? Quantity, bool IsTrinket, bool Resolved);
public sealed record CreatureDetails(SlotAddress Slot, ImmutableArray<string> Types, int? MergeProgress,
    int? SellPrice, decimal? Cooldown, bool Resolved);
public sealed record ObservedChoice(int Index, string Label, string? DefinitionId, int? Cost,
    bool? Enabled, string? Effect, bool Resolved);
public sealed record ObservedTarget(SlotAddress Slot, double X, double Y, double Width, double Height, bool Resolved);
public sealed record RecognitionFact(string Name, double Confidence, bool Resolved, string? Note = null);
public sealed record ObservedOfferLevel(int Index, int Level, bool Resolved, string Basis);
public sealed record GameGeometry(long WindowHandle, int ProcessId, int ClientX, int ClientY,
    int ClientWidth, int ClientHeight, int ContentX, int ContentY, int ContentWidth, int ContentHeight, uint Dpi);
