// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EncoDeck.App.Services;

/// <summary>This process's memory figures, and giving memory back while only the tray icon is left.</summary>
internal static partial class ProcessMemory
{
    /// <summary>What Task Manager calls "Memory" (private working set) and the related figures, in bytes.</summary>
    public readonly record struct Snapshot(long PrivateWorkingSet, long WorkingSet, long PrivateBytes, long ManagedHeap, long GcCommitted, int Threads, int Handles, TimeSpan Cpu);

    public static Snapshot Measure()
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        _ = K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb);
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new Snapshot(
            (long)counters.PrivateWorkingSetSize,
            (long)counters.WorkingSetSize,
            (long)counters.PrivateUsage,
            gc.HeapSizeBytes,
            gc.TotalCommittedBytes,
            process.Threads.Count,
            process.HandleCount,
            process.TotalProcessorTime);
    }

    /// <summary>
    /// Compacts the managed heap, returns its free memory to the system and trims the working set.
    /// Only for when no window is open: the pages the UI needs come back when a window opens again.
    /// </summary>
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        // Finalizers release WinRT/XAML objects of closed windows; collect what they freed too.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _ = SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32GetProcessMemoryInfo(nint process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint process, nint minimum, nint maximum, uint flags);
}
