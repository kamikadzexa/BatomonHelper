using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.GameReader;

public enum MemoryValueType { Int32, UInt32, Byte, Boolean }

public sealed record MemoryScalarField(string Name, long[] PointerOffsets, MemoryValueType ValueType);
public sealed record UnitArrayLayout(long[] PointerOffsets, string CountField, int Stride, int MonsterIdOffset, int LevelOffset, int ShinyOffset, int PositionOffset);
public sealed record ShopArrayLayout(long[] PointerOffsets, string CountField, int Stride, int OfferIdOffset, int RarityOffset, int PriceOffset, int ShinyOffset, int KindOffset);
public sealed record IdArrayLayout(long[] PointerOffsets, string CountField, int Stride, int IdOffset);
public sealed record CaptureEvidence(
    DateTimeOffset CapturedAt,
    ImmutableArray<string> CompletedActions,
    bool RestartValidated,
    decimal Confidence,
    ImmutableArray<string> Notes);

public sealed record MemoryProfile(
    string ProfileId,
    string SteamBuildId,
    string ExecutableSha256,
    long ModuleBaseRva,
    ImmutableDictionary<string, MemoryScalarField> Scalars,
    UnitArrayLayout? Team,
    UnitArrayLayout? Bench,
    ShopArrayLayout? Shop,
    IdArrayLayout? Trinkets,
    IdArrayLayout? Items,
    ImmutableDictionary<int, string> MonsterIds,
    ImmutableDictionary<int, string> TrainerIds,
    ImmutableDictionary<int, string> ActiveSets,
    ImmutableDictionary<int, string> TrinketIds,
    ImmutableDictionary<int, string> ItemIds,
    CaptureEvidence? CaptureEvidence = null)
{
    public static readonly string[] RequiredShopCoreScalars =
    [
        "Day", "ShopRank", "Gold", "Wins", "Lives", "ItemUses", "HasEligibleItemOffer",
        "IsShopLocked", "FreeRerolls", "IsOverflow", "TeamCount", "BenchCount", "ShopCount"
    ];

    public bool IsComplete => RequiredShopCoreScalars.All(Scalars.ContainsKey) && Team is not null && Bench is not null && Shop is not null;
}

public sealed class MemoryProfileStore(string directory)
{
    private readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<MemoryProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return [];
        var profiles = new List<MemoryProfile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            await using var stream = File.OpenRead(file);
            var profile = await JsonSerializer.DeserializeAsync<MemoryProfile>(stream, _options, cancellationToken);
            if (profile is not null) profiles.Add(profile);
        }
        return profiles;
    }
}
