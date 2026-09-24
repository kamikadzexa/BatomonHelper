using BatomonHelper.Core;
using System.Collections.Immutable;

namespace BatomonHelper.ScreenReader;

public sealed record RecognitionCandidate(string Value, double Confidence);
public sealed record RecognizedField(string Name, string Value, double Confidence, bool IsResolved, ImmutableArray<RecognitionCandidate> Candidates, string? Diagnostic = null);
public sealed record CaptureDiagnostic(string Code, string Message, bool IsError);
public sealed record CaptureFrame(byte[] Png, int Width, int Height);

public sealed record ShopCaptureResult(
    ShopState? State,
    CaptureFrame? Frame,
    ImmutableArray<RecognizedField> Fields,
    ImmutableArray<CaptureDiagnostic> Diagnostics)
{
    public bool IsShop => State is not null && !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

public interface IShopScreenReader
{
    Task<ShopCaptureResult> CaptureAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default);
}
