using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The OS process-tree walk: one toolhelp snapshot of all processes, mapped to
/// a parent → children dictionary. The resolver's BFS policy consumes this
/// map; the I/O is isolated so the policy is testable with an in-memory fake.
/// </summary>
internal sealed class ProcessTreeSource
{
    private const uint Th32csSnapprocess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>Takes one toolhelp snapshot and returns the parent → children
    /// map. An empty map when the snapshot fails (no foreground window, or the
    /// process table is unavailable).</summary>
    public Dictionary<int, List<int>> SnapshotParentMap()
    {
        var parentMap = new Dictionary<int, List<int>>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return parentMap;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return parentMap;
            }

            do
            {
                int parentPid = (int)entry.ParentProcessId;
                if (!parentMap.TryGetValue(parentPid, out var children))
                {
                    children = [];
                    parentMap[parentPid] = children;
                }
                children.Add((int)entry.ProcessId);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parentMap;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint CntUsage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint CntThreads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    // Entry points are spelled explicitly so the binding resolves to the
    // spelled export and a method rename cannot silently change what is
    // called (ADR-0020); PInvokeBindingTests probes each pair against the
    // real DLL at the gate.
    [DllImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", EntryPoint = "Process32First", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32Next", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
