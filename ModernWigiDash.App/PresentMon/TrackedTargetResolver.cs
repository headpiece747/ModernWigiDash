using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Resolves the process to track for the FPS readout: the foreground window's
/// process, expanded to its descendant tree (multi-process apps present from
/// child GPU/renderer processes), else none. The producer asks one question.
/// </summary>
public sealed class TrackedTargetResolver
{
    private const uint Th32csSnapprocess = 0x00000002;

    /// <summary>Bound on the process-tree walk: multi-process apps (Chrome,
    /// Edge, Electron) can fan out hundreds of processes, but the presenting
    /// descendants are near the root — 32 captures them without a per-tick
    /// enumeration cost.</summary>
    internal const int MaxCandidateProcesses = 32;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly Func<int> _foregroundPidProvider;
    private readonly Func<int, IReadOnlyList<int>> _childrenProvider;

    public TrackedTargetResolver()
        : this(GetForegroundPidFromUser32, GetChildrenFromToolhelp)
    {
    }

    /// <summary>Test seam: injects the foreground lookup and the process-tree
    /// navigation so tests drive the resolution without real processes.</summary>
    internal TrackedTargetResolver(
        Func<int> foregroundPidProvider,
        Func<int, IReadOnlyList<int>> childrenProvider)
    {
        _foregroundPidProvider = foregroundPidProvider;
        _childrenProvider = childrenProvider;
    }

    /// <summary>Foreground window's process id, or 0 when no foreground window.</summary>
    public int GetForegroundProcessId() => _foregroundPidProvider();

    /// <summary>
    /// The foreground pid plus its descendant pids (toolhelp snapshot), root
    /// first in stable discovery order. Empty when there is no foreground
    /// window or the foreground window belongs to this process.
    /// </summary>
    public IReadOnlyList<int> ResolveCandidates()
    {
        int rootPid = _foregroundPidProvider();
        if (rootPid <= 0 || rootPid == Environment.ProcessId)
        {
            return [];
        }

        List<int> candidates = [rootPid];
        HashSet<int> seen = [rootPid];
        Queue<int> frontier = new();
        frontier.Enqueue(rootPid);

        while (frontier.Count > 0 && candidates.Count < MaxCandidateProcesses)
        {
            int pid = frontier.Dequeue();
            foreach (int child in _childrenProvider(pid))
            {
                if (child <= 0 || !seen.Add(child))
                {
                    continue;
                }

                candidates.Add(child);
                frontier.Enqueue(child);
                if (candidates.Count >= MaxCandidateProcesses)
                {
                    return candidates;
                }
            }
        }

        return candidates;
    }

    /// <summary>True when the pid is this process — never a tracking target.</summary>
    public static bool IsOwnProcess(int pid) => pid == Environment.ProcessId;

    private static int GetForegroundPidFromUser32()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>Direct children (ParentProcessId == parentPid) via one toolhelp
    /// snapshot of all processes.</summary>
    private static IReadOnlyList<int> GetChildrenFromToolhelp(int parentPid)
    {
        List<int> children = [];
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return children;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return children;
            }

            do
            {
                if (entry.ParentProcessId == parentPid)
                {
                    children.Add((int)entry.ProcessId);
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return children;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
