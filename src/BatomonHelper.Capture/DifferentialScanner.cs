using BatomonHelper.GameReader;
using System.Buffers.Binary;
using System.Collections.Immutable;

namespace BatomonHelper.Capture;

public sealed class DifferentialScanner
{
    public const int MaxCandidatesPerField = 20_000;
    private const int BufferSize = 1024 * 1024;

    public IReadOnlyDictionary<string, ImmutableArray<CandidateAddress>> ScanBaseline(WindowsProcessMemory memory, IReadOnlyDictionary<string, int> expected)
    {
        var matches = expected.Keys.ToDictionary(key => key, _ => ImmutableArray.CreateBuilder<CandidateAddress>(), StringComparer.OrdinalIgnoreCase);
        var wanted = expected.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        var buffer = new byte[BufferSize];
        foreach (var region in memory.EnumerateScannableRegions())
        {
            for (nuint consumed = 0; consumed < region.Size;)
            {
                var count = (int)Math.Min((nuint)buffer.Length, region.Size - consumed);
                var address = (IntPtr)(region.BaseAddress.ToInt64() + checked((long)consumed));
                if (memory.TryRead(address, buffer.AsSpan(0, count)))
                {
                    for (var offset = 0; offset <= count - sizeof(int); offset += sizeof(int))
                    {
                        var value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, sizeof(int)));
                        if (!wanted.TryGetValue(value, out var fields)) continue;
                        var candidateAddress = address.ToInt64() + offset;
                        foreach (var field in fields)
                        {
                            if (matches[field].Count < MaxCandidatesPerField) matches[field].Add(new(candidateAddress, value));
                        }
                    }
                }
                consumed += (nuint)count;
            }
        }
        return matches.ToDictionary(pair => pair.Key, pair => pair.Value.ToImmutable(), StringComparer.OrdinalIgnoreCase);
    }

    public CaptureSession Filter(WindowsProcessMemory memory, CaptureSession session, IReadOnlyDictionary<string, int> expected, string action)
    {
        var fields = session.Fields.ToBuilder();
        foreach (var (name, current) in session.Fields)
        {
            if (!expected.TryGetValue(name, out var expectedValue)) continue;
            var candidates = current.Candidates
                .Where(candidate => memory.TryReadInt32((IntPtr)candidate.Address, out var actual) && actual == expectedValue)
                .Select(candidate => new CandidateAddress(candidate.Address, expectedValue))
                .ToImmutableArray();
            fields[name] = new(expectedValue, candidates);
        }
        return session with { Actions = session.Actions.Add(action), Fields = fields.ToImmutable() };
    }

    public IReadOnlyDictionary<string, IReadOnlyList<(long Address, int Value)>> Observe(WindowsProcessMemory memory, CaptureSession session)
    {
        return session.Fields.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<(long Address, int Value)>)pair.Value.Candidates
                .Select(candidate => memory.TryReadInt32((IntPtr)candidate.Address, out var value) ? (candidate.Address, value) : (candidate.Address, int.MinValue))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
