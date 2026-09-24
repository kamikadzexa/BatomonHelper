using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.GameReader;

public sealed record GameReadResult(RunState? State, string Status, bool IsUnverified, IReadOnlyList<string> Errors)
{
    public static GameReadResult NotRunning { get; } = new(null, "Waiting for batomon_showdown.exe", false, []);
}

public interface IGameStateReader
{
    Task<GameReadResult> ReadAsync(MemoryProfile? profile, bool allowStaleProfile, CancellationToken cancellationToken = default);
}

public sealed class ProfiledGameStateReader : IGameStateReader
{
    public Task<GameReadResult> ReadAsync(MemoryProfile? profile, bool allowStaleProfile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var memory = WindowsProcessMemory.TryOpenGame();
        if (memory is null) return Task.FromResult(GameReadResult.NotRunning);
        if (profile is null || !profile.IsComplete) return Task.FromResult(new GameReadResult(null, "No complete memory profile is installed for this build.", false, ["A tested profile is required to read live state."]));

        var errors = new List<string>();
        var values = profile.Scalars.ToDictionary(pair => pair.Key, pair => ReadScalar(memory, profile, pair.Value, errors));
        var team = ReadUnits(memory, profile, profile.Team!, values, errors);
        var bench = ReadUnits(memory, profile, profile.Bench!, values, errors);
        var shop = ReadShop(memory, profile, profile.Shop!, values, errors);
        var trinkets = profile.Trinkets is null ? ImmutableArray<string>.Empty : ReadIds(memory, profile, profile.Trinkets, profile.TrinketIds, values, errors, "trinket");
        var items = profile.Items is null ? ImmutableArray<string>.Empty : ReadIds(memory, profile, profile.Items, profile.ItemIds, values, errors, "item");
        if (errors.Count > 0) return Task.FromResult(new GameReadResult(null, "The memory profile could not read a consistent state.", allowStaleProfile, errors));

        var state = new RunState(
            Value("Day", 1), Value("ShopRank", 1), Value("Gold", 0), Value("Wins", 0), Value("Lives", 0),
            profile.ActiveSets.GetValueOrDefault(Value("ActiveSet", 0), "starter"),
            profile.TrainerIds.GetValueOrDefault(Value("Trainer", 0)), Value("ItemUses", 0), Value("HasEligibleItemOffer", 0) != 0,
            Value("IsShopLocked", 0) != 0, Value("FreeRerolls", 0), Value("IsOverflow", 0) != 0,
            team, bench, shop, trinkets, items,
            new(Value("ResearchNotes", 0), Value("VipPass", 0) != 0, Value("MysticIncenseQueued", 0) != 0,
                Value("ForceAllMythical", 0) != 0, TicketRarity(Value("TicketRarity", -1)), Value("RainbowPearl", 0) != 0, Math.Max(1, Value("ShinyMultiplier", 1))));
        return Task.FromResult(new GameReadResult(state, allowStaleProfile ? "Live state read with an unverified memory profile." : "Live state read.", allowStaleProfile, []));

        int Value(string key, int fallback) => values.TryGetValue(key, out var value) ? value : fallback;
    }

    private static int ReadScalar(WindowsProcessMemory memory, MemoryProfile profile, MemoryScalarField field, List<string> errors)
    {
        var address = ResolveAddress(memory, profile, field.PointerOffsets, errors, field.Name);
        if (address == IntPtr.Zero) { errors.Add($"Could not resolve {field.Name}."); return 0; }
        if (field.ValueType is MemoryValueType.Byte or MemoryValueType.Boolean)
        {
            if (!memory.TryReadByte(address, out var byteValue)) { errors.Add($"Could not read {field.Name}."); return 0; }
            return field.ValueType == MemoryValueType.Boolean ? byteValue == 0 ? 0 : 1 : byteValue;
        }
        if (!memory.TryReadInt32(address, out var value)) { errors.Add($"Could not read {field.Name}."); return 0; }
        return value;
    }

    private static ImmutableArray<UnitState> ReadUnits(WindowsProcessMemory memory, MemoryProfile profile, UnitArrayLayout layout, IReadOnlyDictionary<string, int> values, List<string> errors)
    {
        var count = values.GetValueOrDefault(layout.CountField);
        if (count is < 0 or > 10) { errors.Add($"{layout.CountField} is outside the valid unit count range."); return []; }
        var start = ResolveAddress(memory, profile, layout.PointerOffsets, errors, layout.CountField + " array");
        if (start == IntPtr.Zero) return [];
        var units = ImmutableArray.CreateBuilder<UnitState>();
        for (var index = 0; index < count; index++)
        {
            var record = IntPtr.Add(start, checked(index * layout.Stride));
            if (!ReadInt(memory, IntPtr.Add(record, layout.MonsterIdOffset), out var id) || !ReadInt(memory, IntPtr.Add(record, layout.LevelOffset), out var level) ||
                !ReadInt(memory, IntPtr.Add(record, layout.ShinyOffset), out var shiny) || !ReadInt(memory, IntPtr.Add(record, layout.PositionOffset), out var position))
            { errors.Add("A unit record was truncated."); return []; }
            if (!profile.MonsterIds.TryGetValue(id, out var monsterId)) { errors.Add($"Unknown monster numeric id {id}."); return []; }
            units.Add(new(monsterId, level, shiny != 0, position, [], ImmutableDictionary<string, int>.Empty));
        }
        return units.ToImmutable();
    }

    private static ImmutableArray<ShopSlotState> ReadShop(WindowsProcessMemory memory, MemoryProfile profile, ShopArrayLayout layout, IReadOnlyDictionary<string, int> values, List<string> errors)
    {
        var count = values.GetValueOrDefault(layout.CountField);
        if (count is < 0 or > 5) { errors.Add("Shop count is outside the valid range."); return []; }
        var start = ResolveAddress(memory, profile, layout.PointerOffsets, errors, "shop array");
        if (start == IntPtr.Zero) return [];
        var slots = ImmutableArray.CreateBuilder<ShopSlotState>();
        for (var index = 0; index < count; index++)
        {
            var record = IntPtr.Add(start, checked(index * layout.Stride));
            if (!ReadInt(memory, IntPtr.Add(record, layout.KindOffset), out var kind) || !ReadInt(memory, IntPtr.Add(record, layout.OfferIdOffset), out var offer) ||
                !ReadInt(memory, IntPtr.Add(record, layout.RarityOffset), out var rarity) || !ReadInt(memory, IntPtr.Add(record, layout.PriceOffset), out var price) || !ReadInt(memory, IntPtr.Add(record, layout.ShinyOffset), out var shiny))
            { errors.Add("A shop record was truncated."); return []; }
            var slotKind = Enum.IsDefined(typeof(ShopSlotKind), kind) ? (ShopSlotKind)kind : ShopSlotKind.Empty;
            profile.MonsterIds.TryGetValue(offer, out var offerId);
            slots.Add(new(slotKind, offerId, Enum.IsDefined(typeof(Rarity), rarity) ? (Rarity)rarity : null, price, shiny != 0));
        }
        return slots.ToImmutable();
    }

    private static ImmutableArray<string> ReadIds(WindowsProcessMemory memory, MemoryProfile profile, IdArrayLayout layout, IReadOnlyDictionary<int, string> idMap, IReadOnlyDictionary<string, int> values, List<string> errors, string label)
    {
        var count = values.GetValueOrDefault(layout.CountField);
        if (count is < 0 or > 32) { errors.Add($"{label} count is outside the valid range."); return []; }
        var start = ResolveAddress(memory, profile, layout.PointerOffsets, errors, label + " array");
        if (start == IntPtr.Zero) return [];
        var ids = ImmutableArray.CreateBuilder<string>();
        for (var index = 0; index < count; index++)
        {
            if (!ReadInt(memory, IntPtr.Add(start, checked(index * layout.Stride + layout.IdOffset)), out var id) || !idMap.TryGetValue(id, out var value))
            { errors.Add($"Unknown {label} numeric id {id}."); return []; }
            ids.Add(value);
        }
        return ids.ToImmutable();
    }

    private static IntPtr ResolveAddress(WindowsProcessMemory memory, MemoryProfile profile, IReadOnlyList<long> offsets, List<string> errors, string name)
    {
        if (offsets.Count == 0) { errors.Add($"{name} has no pointer offsets."); return IntPtr.Zero; }
        var current = (ulong)memory.BaseAddress.ToInt64() + (ulong)profile.ModuleBaseRva + (ulong)offsets[0];
        for (var index = 1; index < offsets.Count; index++)
        {
            if (!memory.TryReadUInt64((IntPtr)current, out current)) { errors.Add($"Pointer chain failed for {name}."); return IntPtr.Zero; }
            current += (ulong)offsets[index];
        }
        return (IntPtr)current;
    }

    private static bool ReadInt(WindowsProcessMemory memory, IntPtr address, out int value) => memory.TryReadInt32(address, out value);
    private static Rarity? TicketRarity(int value) => Enum.IsDefined(typeof(Rarity), value) ? (Rarity)value : null;
}
