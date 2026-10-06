using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Drawing;

namespace BatomonHelper.ScreenReader;

public sealed record BoardCaptureTimings(double RecognitionMilliseconds, int OcrCalls, int CacheHits,
    double OcrMilliseconds = 0, double AcquisitionMilliseconds = 0, int SpriteCacheHits = 0);
public sealed record BoardCaptureResult(BoardSnapshot? Snapshot, CaptureFrame? Frame,
    ImmutableArray<RecognizedField> Fields, ImmutableArray<CaptureDiagnostic> Diagnostics,
    BoardCaptureTimings Timings)
{
    public bool RequiresReview => Fields.Any(entry => !entry.IsResolved);
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CaptureFrame>? FrameEncoder { get; init; }
    public BoardCaptureResult MaterializeFrame() => Frame is not null || FrameEncoder is null ? this : this with { Frame = FrameEncoder(), FrameEncoder = null };
    public bool IsShop => Snapshot is not null && !Diagnostics.Any(diagnostic => diagnostic.IsError);
}
public interface IBoardScreenReader
{
    event Action? FrameCaptured;
    Task<BoardCaptureResult> CaptureAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default);
    Task<BoardCaptureResult> RecognizeImageAsync(Bitmap image, CatalogSnapshot catalog, CancellationToken cancellationToken = default);
}

public static class BoardLayout
{
    private static NormalizedRect R(double x, double y, double width, double height) => new(x / 2048, y / 1152, width / 2048, height / 1152);
    public static readonly NormalizedRect Day = R(860, 18, 339, 76);
    public static readonly NormalizedRect Lives = R(746, 32, 70, 60);
    public static readonly NormalizedRect Wins = R(1300, 31, 210, 59);
    public static readonly NormalizedRect Hp = R(110, 789, 309, 35);
    public static readonly NormalizedRect Gold = R(954, 872, 138, 44);
    public static readonly NormalizedRect Rank = R(411, 870, 82, 31);
    public static readonly NormalizedRect Reroll = R(17, 1055, 300, 61);
    public static readonly NormalizedRect ItemUses = R(1414, 962, 48, 32);
    public static readonly NormalizedRect Lock = R(1480, 860, 144, 41);
    public static readonly NormalizedRect TrainerPortrait = R(29, 211, 387, 252);
    public static readonly NormalizedRect[] Odds = [R(521, 870, 54, 31), R(597, 870, 47, 31), R(672, 870, 47, 31), R(750, 870, 47, 31), R(827, 870, 47, 31)];
    public static readonly NormalizedRect[] Team = [R(649, 475, 199, 156), R(878, 475, 199, 156), R(1107, 475, 199, 156), R(649, 661, 199, 156), R(878, 661, 199, 156), R(1107, 661, 199, 156)];
    public static readonly NormalizedRect[] Bench = [R(524, 212, 199, 156), R(754, 212, 199, 156), R(984, 212, 199, 156), R(1214, 212, 199, 156)];
    public static readonly NormalizedRect[] Shop = [R(411, 933, 223, 145), R(661, 933, 223, 145), R(910, 933, 223, 145), R(1160, 933, 223, 145), R(1409, 933, 223, 145)];
    public static NormalizedRect Sprite(NormalizedRect slot, bool shop) => slot with { Height = slot.Height - .023 };
    public static NormalizedRect Badges(NormalizedRect slot, bool shop) => slot with { Y = slot.Y + slot.Height - .023, Height = .031 };
    public static NormalizedRect Level(NormalizedRect slot) => new(slot.X + 60d / 2048, slot.Y, 18d / 2048, 25d / 1152);
    public static NormalizedRect ShopLevelHeader(NormalizedRect slot) => new(slot.X + 8d / 2048, slot.Y + 8d / 1152, 35d / 2048, 32d / 1152);
    public static NormalizedRect ShopLevel(NormalizedRect slot) => new(slot.X + 62d / 2048, slot.Y + 8d / 1152, 24d / 2048, 32d / 1152);
    public static NormalizedRect Price(NormalizedRect slot) => new(slot.X + slot.Width - .025, .940, .025, .028);
    public static NormalizedRect Name(NormalizedRect slot) => new(slot.X, .937, slot.Width - .026, .037);
}
