using BatomonHelper.Core;
using System.Drawing;

namespace BatomonHelper.ScreenReader;

/// <summary>Compatibility facade for callers using the V1 odds-only contract.</summary>
public sealed class WindowsShopScreenReader : IShopScreenReader
{
    private readonly WindowsBoardScreenReader _reader = new();
    public async Task<ShopCaptureResult> CaptureAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default) =>
        await Project(await _reader.CaptureAsync(catalog, cancellationToken), catalog);
    public async Task<ShopCaptureResult> RecognizeImageAsync(Bitmap image, CatalogSnapshot catalog, CancellationToken cancellationToken = default) =>
        await Project(await _reader.RecognizeImageAsync(image, catalog, cancellationToken), catalog);

    private static async Task<ShopCaptureResult> Project(BoardCaptureResult result, CatalogSnapshot catalog)
    {
        result = await Task.Run(result.MaterializeFrame);
        var board = result.Snapshot;
        if (board is null) return new(null, result.Frame, result.Fields, result.Diagnostics);
        var item = board.Shop.FirstOrDefault(slot => slot.Occupancy == SlotOccupancy.Item);
        var definition = catalog.Items.FirstOrDefault(entry => entry.Id == item?.DefinitionId);
        // V1 accepts partial recognition proposals for its correction dialog. Preserve nulls
        // where its contract permits them and keep the V2 confidence fields alongside them.
        var snapshot = new ShopOddsSnapshot(catalog.Trainers.FirstOrDefault(trainer => trainer.Id == board.TrainerId)?.Name,
            board.ShopRank ?? 0, board.Gold ?? 0, board.RarityOdds, board.ItemUsesRemaining > 0,
            definition is null || item?.Price is null ? null : new(definition.Name, item.Price.Value, definition.RuleText), board.CapturedAt);
        return new(snapshot, result.Frame, result.Fields, result.Diagnostics);
    }
}
