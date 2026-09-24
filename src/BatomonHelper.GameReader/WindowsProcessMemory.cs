using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatomonHelper.GameReader;

public sealed class WindowsProcessMemory : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly IntPtr _handle;

    private WindowsProcessMemory(Process process)
    {
        Process = process;
        _handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, process.Id);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the game process for read-only access.");
    }

    public Process Process { get; }
    public IntPtr BaseAddress => Process.MainModule?.BaseAddress ?? IntPtr.Zero;
    public int MainModuleSize => Process.MainModule?.ModuleMemorySize ?? 0;
    internal IntPtr Handle => _handle;

    public static WindowsProcessMemory? TryOpenGame()
    {
        var process = Process.GetProcessesByName("batomon_showdown").FirstOrDefault();
        return process is null ? null : new WindowsProcessMemory(process);
    }

    public unsafe bool TryRead(IntPtr address, Span<byte> destination)
    {
        fixed (byte* pointer = destination)
        {
            return ReadProcessMemory(_handle, address, (IntPtr)pointer, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
        }
    }

    public IEnumerable<MemoryRegion> EnumerateScannableRegions(bool includeMainModuleImage = false)
    {
        var current = 0UL;
        var maximum = Environment.Is64BitProcess ? 0x0000_7FFF_FFFF_FFFFUL : 0x7FFF_FFFFUL;
        var moduleStart = (ulong)BaseAddress.ToInt64();
        var moduleEnd = moduleStart + (ulong)Math.Max(0, MainModuleSize);
        while (current < maximum)
        {
            var result = VirtualQueryEx(_handle, (IntPtr)current, out var information, (nuint)Marshal.SizeOf<MemoryBasicInformation>());
            if (result == 0) yield break;
            var start = (ulong)information.BaseAddress.ToInt64();
            var length = (ulong)information.RegionSize;
            if (length == 0 || start + length <= current) yield break;
            var committed = information.State == 0x1000;
            var protectedReadable = information.Protect is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
            var guarded = (information.Protect & 0x100) != 0 || (information.Protect & 0x01) != 0;
            var privateWritable = information.Type == 0x20000 && information.Protect is 0x04 or 0x08 or 0x40 or 0x80;
            var isMainModule = includeMainModuleImage && start >= moduleStart && start < moduleEnd;
            if (committed && protectedReadable && !guarded && (privateWritable || isMainModule))
                yield return new MemoryRegion((IntPtr)start, checked((nuint)length), information.Type, information.Protect);
            current = start + length;
        }
    }

    public bool TryReadInt32(IntPtr address, out int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        value = 0;
        if (!TryRead(address, buffer)) return false;
        value = BitConverter.ToInt32(buffer);
        return true;
    }

    public bool TryReadByte(IntPtr address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        value = 0;
        if (!TryRead(address, buffer)) return false;
        value = buffer[0];
        return true;
    }

    public bool TryReadUInt64(IntPtr address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        value = 0;
        if (!TryRead(address, buffer)) return false;
        value = BitConverter.ToUInt64(buffer);
        return true;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        Process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint numberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation buffer, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}

public readonly record struct MemoryRegion(IntPtr BaseAddress, nuint Size, uint Type, uint Protect);
