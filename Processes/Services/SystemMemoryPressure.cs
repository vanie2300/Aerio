using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TabloidBrowser;

/// <summary>
/// Machine-wide physical memory pressure signal. The pure decision overload is
/// unit-testable; the parameterless probe reads real system state via Win32.
/// </summary>
public static class SystemMemoryPressure
{
    /// <summary>Pressure threshold: max(1 GB, 1/12 of installed physical memory).</summary>
    public static ulong PressureThresholdBytes(ulong totalPhysical)
    {
        return Math.Max(1024UL * 1024 * 1024, totalPhysical / 12);
    }

    public static bool IsUnderPressure(ulong availablePhysical, ulong totalPhysical)
    {
        return availablePhysical < PressureThresholdBytes(totalPhysical);
    }

    public static bool IsUnderPressure()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            Log.Error(new Win32Exception(Marshal.GetLastWin32Error()), "Unable to read system memory status");
            return false;
        }

        return IsUnderPressure(status.AvailablePhysical, status.TotalPhysical);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
