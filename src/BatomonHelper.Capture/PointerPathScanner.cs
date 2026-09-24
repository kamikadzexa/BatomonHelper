using BatomonHelper.GameReader;
using System.Buffers.Binary;
using System.Collections.Immutable;

namespace BatomonHelper.Capture;

public sealed class PointerPathScanner
{
    private const int BufferSize = 1024 * 1024;
    private const long MaxOffset = 0x400;
    private const int MaxFrontier = 128;

    public ImmutableArray<PointerPath> FindModuleRootPaths(WindowsProcessMemory memory, long targetAddress, int maxDepth = 3)
    {
        var moduleStart = memory.BaseAddress.ToInt64();
        var moduleEnd = moduleStart + memory.MainModuleSize;
        var paths = ImmutableArray.CreateBuilder<PointerPath>();
        var frontier = new List<(long Target, ImmutableArray<long> Tail)> { (targetAddress, []) };
        var seen = new HashSet<long> { targetAddress };

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<(long Target, ImmutableArray<long> Tail)>();
            foreach (var node in frontier)
            {
                foreach (var source in FindIncomingPointers(memory, node.Target))
                {
                    var offset = node.Target - source.PointedValue;
                    if (source.Address >= moduleStart && source.Address < moduleEnd)
                    {
                        paths.Add(new(node.Target, [source.Address - moduleStart, offset, .. node.Tail], depth));
                    }
                    else if (next.Count < MaxFrontier && seen.Add(source.Address))
                    {
                        next.Add((source.Address, ImmutableArray.Create(offset).AddRange(node.Tail)));
                    }
                }
            }
            frontier = next;
        }
        return paths.DistinctBy(path => string.Join(',', path.Offsets)).ToImmutableArray();
    }

    private static IEnumerable<(long Address, long PointedValue)> FindIncomingPointers(WindowsProcessMemory memory, long target)
    {
        var buffer = new byte[BufferSize];
        foreach (var region in memory.EnumerateScannableRegions(includeMainModuleImage: true))
        {
            for (nuint consumed = 0; consumed < region.Size;)
            {
                var count = (int)Math.Min((nuint)buffer.Length, region.Size - consumed);
                var address = (IntPtr)(region.BaseAddress.ToInt64() + checked((long)consumed));
                if (memory.TryRead(address, buffer.AsSpan(0, count)))
                {
                    var first = (int)((8 - (address.ToInt64() & 7)) & 7);
                    for (var offset = first; offset <= count - sizeof(long); offset += sizeof(long))
                    {
                        var pointedValue = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, sizeof(long)));
                        if (pointedValue <= target && target - pointedValue <= MaxOffset) yield return (address.ToInt64() + offset, pointedValue);
                    }
                }
                consumed += (nuint)count;
            }
        }
    }
}
