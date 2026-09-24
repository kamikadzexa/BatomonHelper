using BatomonHelper.GameReader;
using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Capture;

public sealed record CandidateAddress(long Address, int LastObservedValue);
public sealed record CapturedField(int ExpectedValue, ImmutableArray<CandidateAddress> Candidates);
public sealed record PointerPath(long TargetAddress, ImmutableArray<long> Offsets, int Depth);
public sealed record CaptureSession(
    string SteamBuildId,
    string ExecutableSha256,
    DateTimeOffset CreatedAt,
    ImmutableArray<string> Actions,
    ImmutableDictionary<string, CapturedField> Fields);

public static class CaptureSessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Choose a capture session file first.");

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    public static async Task SaveAsync(string path, CaptureSession session, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"The session file path has no directory: {fullPath}");
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(stream, session, Options, cancellationToken);
    }

    public static async Task<CaptureSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("No capture session exists at this path. Enter the visible values and choose Baseline scan first.", fullPath);

        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<CaptureSession>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Capture session is empty or invalid.");
    }
}
