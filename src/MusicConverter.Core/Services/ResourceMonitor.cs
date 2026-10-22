using System.Runtime.InteropServices;

namespace MusicConverter.Services;

/// <summary>Reads how loaded the machine's memory is, to let a batch back off before the system starts thrashing.</summary>
public static class ResourceMonitor
{
    /// <returns>Percentage of physical memory in use (0–100), or null when it cannot be determined.</returns>
    public static int? GetMemoryLoadPercent()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : null;
        }

        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 ? (int)(info.MemoryLoadBytes * 100 / info.TotalAvailableMemoryBytes) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
