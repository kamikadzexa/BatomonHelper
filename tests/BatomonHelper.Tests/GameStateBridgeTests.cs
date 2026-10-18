using BatomonHelper.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BatomonHelper.Tests;

public sealed class GameStateBridgeTests
{
    private static async Task<CatalogSnapshot> Catalog() => (await new SnapshotRepository(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-v1.json")))!.AsObject();
    private static DateTimeOffset Time(JsonObject json) => DateTimeOffset.Parse(json["updatedAt"]!.GetValue<string>());

    [Fact]
    public async Task Reads_snapshot_exported_by_installed_game_objects()
    {
        var json = Fixture();
        json["board"]!["regionFilter"] = "Jinto";
        var result = GameStateBridge.Parse(json.ToJsonString(), await Catalog(), Time(json));
        var board = result.Board!;
        Assert.Equal(6, board.Team.Length);
        Assert.Equal(4, board.Bench.Length);
        Assert.Equal(5, board.Shop.Length);
        Assert.Equal("redhead", board.TrainerId);
        Assert.Equal(ShinyState.Shiny, board.Team[0].Variant);
        Assert.Equal("scorchimp", board.Team[0].DefinitionId);
        Assert.Contains(board.Team[0].Stats, stat => stat.Kind == VisibleStatKind.Damage && stat.Value == 23);
        Assert.Equal("dribblet", board.Bench[3].DefinitionId);
        Assert.Equal(13, board.Shop[0].Price); // Game price after the -$2 modifier.
        Assert.False(board.Shop[0].Affordable);
        Assert.Equal(SlotOccupancy.Empty, board.Shop[1].Occupancy);
        Assert.Equal("cake", board.Shop[4].DefinitionId);
        Assert.Equal(7, board.Shop[4].Price);
        Assert.True(board.Shop[4].Affordable);
        Assert.Equal(2, board.FreeRerolls);
        Assert.Null(board.RerollCost);
        Assert.Equal(2, board.ItemUsesRemaining);
        Assert.Equal(3, board.ItemUsesMaximum);
        Assert.True(board.ShopLocked);
        Assert.Equal(.55m, board.RarityOdds[0].Probability);
        Assert.Equal(10, board.ToShopOddsSnapshot(await Catalog()).Gold);
        Assert.Equal(MonsterRegionFilter.Jinto, board.RegionFilter);
    }

    [Fact]
    public async Task Rejects_stale_future_and_unsupported_messages()
    {
        var json = Fixture(); var now = Time(json); var catalog = await Catalog();
        Assert.Throws<InvalidDataException>(() => GameStateBridge.Parse(json.ToJsonString(), catalog, now.AddSeconds(6)));
        Assert.Throws<InvalidDataException>(() => GameStateBridge.Parse(json.ToJsonString(), catalog, now.AddSeconds(-6)));
        json["protocolVersion"] = 2;
        Assert.Throws<InvalidDataException>(() => GameStateBridge.Parse(json.ToJsonString(), catalog, now));
    }

    [Fact]
    public async Task Rejects_partial_unknown_and_inconsistent_boards()
    {
        var catalog = await Catalog();
        foreach (var mutate in new Action<JsonObject>[] {
            json => json["board"]!["team"]!.AsArray().RemoveAt(0),
            json => json["board"]!["shop"]![0]!["definitionId"] = "unknown_new_species",
            json => json["board"]!["shop"]![0]!["affordable"] = true,
            json => json["board"]!["rarityOdds"]![0]!["probability"] = .9m,
            json => json["board"] = null,
            json => json["error"] = "Game fields changed"
        })
        {
            var json = Fixture(); mutate(json);
            Assert.Throws<InvalidDataException>(() => GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json)));
        }
    }

    [Fact]
    public async Task Outside_shop_does_not_apply_a_board_and_renamed_species_resolve()
    {
        var json = Fixture(); var catalog = await Catalog();
        json["board"]!["team"]![0]!["definitionId"] = "Electranade";
        Assert.Equal("galvanade", GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json)).Board!.Team[0].DefinitionId);
        json["board"]!["trainerId"] = "res://data/trainers/trainer_redhead.tres";
        Assert.Equal("redhead", GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json)).Board!.TrainerId);
        json["phase"] = "other";
        Assert.Null(GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json)).Board);
        Assert.Throws<JsonException>(() => GameStateBridge.Parse("{", catalog, Time(json)));
    }

    [Fact]
    public async Task Normalizes_only_floating_point_residue_and_rejects_old_board_with_fresh_heartbeat()
    {
        var json = Fixture(); var catalog = await Catalog();
        for (var index = 0; index < 3; index++) json["board"]!["rarityOdds"]![index]!["probability"] = .333333333333333m;
        Assert.Equal(1m, GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json)).Board!.RarityOdds.Sum(value => value.Probability));
        json["board"]!["capturedAt"] = Time(json).AddSeconds(-10).ToString("O");
        Assert.Throws<InvalidDataException>(() => GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json)));
    }

    [Fact]
    public async Task Reads_actual_red_coin_shop_and_normalizes_all_catalog_item_separators()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "game-bridge-red-coin.json")))!.AsObject();
        var catalog = await Catalog();
        var message = GameStateBridge.Parse(json.ToJsonString(), catalog, Time(json));
        Assert.Equal("red-coin", message.Board!.Shop[4].DefinitionId);
        Assert.Equal(0, message.Board.Shop[4].Price);
        Assert.True(message.Board.Shop[4].Affordable);
        foreach (var item in catalog.Items)
            Assert.Equal(item.Id, GameCatalogIds.Resolve(item.Id.Replace('-', '_'), catalog.Items.Select(value => value.Id)));
    }
}
