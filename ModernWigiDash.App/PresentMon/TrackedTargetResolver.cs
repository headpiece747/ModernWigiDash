using System.Runtime.InteropServices;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Resolves the process to track for the FPS readout: the foreground window's
/// process, expanded to its descendant tree (multi-process apps present from
/// child GPU/renderer processes), else none. The producer asks one question.
/// </summary>
internal sealed class TrackedTargetResolver
{
    /// <summary>Bound on the process-tree walk: multi-process apps (Chrome,
    /// Edge, Electron) can fan out hundreds of processes, but the presenting
    /// descendants are near the root — 32 captures them without a per-tick
    /// enumeration cost.</summary>
    internal const int MaxCandidateProcesses = 32;

    private readonly Func<int> _foregroundPidProvider;
    private readonly Func<int, IReadOnlyList<int>>? _childrenProvider;
    private readonly Func<string?>? _titleProvider;
    private readonly ProcessTreeSource _processTree;

    public TrackedTargetResolver()
        : this(GetForegroundPidFromUser32)
    {
    }

    /// <summary>Test seam: injects the foreground lookup and the process-tree
    /// navigation so tests drive the resolution without real processes. When
    /// <paramref name="childrenProvider"/> is omitted the resolver walks the
    /// real toolhelp snapshot once per resolution (see <see cref="ResolveCandidates"/>).
    /// The optional <paramref name="titleProvider"/> makes the diagnostics
    /// path (<see cref="ForegroundWindowTitle"/>) testable; it defaults to the
    /// real user32 title query.</summary>
    internal TrackedTargetResolver(
        Func<int> foregroundPidProvider,
        Func<int, IReadOnlyList<int>>? childrenProvider = null,
        Func<string?>? titleProvider = null,
        ProcessTreeSource? processTree = null)
    {
        _foregroundPidProvider = foregroundPidProvider;
        _childrenProvider = childrenProvider;
        _titleProvider = titleProvider;
        _processTree = processTree ?? new ProcessTreeSource();
    }

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

        // One toolhelp snapshot per resolution: the real process table is
        // materialized into a parent map once, so BFS children lookups never
        // re-enumerate processes. Injected providers (tests) are called per
        // pid as before.
        Dictionary<int, List<int>>? parentMap = _childrenProvider is null ? _processTree.SnapshotParentMap() : null;

        List<int> candidates = [rootPid];
        HashSet<int> seen = [rootPid];
        Queue<int> frontier = new();
        frontier.Enqueue(rootPid);

        while (frontier.Count > 0 && candidates.Count < MaxCandidateProcesses)
        {
            int pid = frontier.Dequeue();
            foreach (int child in ChildrenOf(pid, parentMap))
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

    /// <summary>Children of <paramref name="pid"/>: from the resolution-wide
    /// parent map when walking the real tree, else from the injected provider.</summary>
    private IReadOnlyList<int> ChildrenOf(int pid, Dictionary<int, List<int>>? parentMap)
    {
        if (parentMap is null)
        {
            return _childrenProvider!(pid);
        }
        if (parentMap.TryGetValue(pid, out var children))
        {
            return children;
        }
        return [];
    }

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

    /// <summary>
    /// The foreground window's title, capped (diagnostics only): tells which
    /// window Windows considers foreground when the resolved target does not
    /// change as expected — the title identifies the game/app at a glance.
    /// Injectable via the seam; the default is the real user32 query.
    /// </summary>
    internal string? ForegroundWindowTitle()
        => _titleProvider is not null ? _titleProvider() : GetForegroundWindowTitle();

    private static string? GetForegroundWindowTitle()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var sb = new System.Text.StringBuilder(64);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // The method name is the bare export, but the attribute's
    // CharSet.Unicode already resolves the call to the W export (a full
    // window title comes back, which the A export would mangle); the
    // spelling keeps that resolution a construction fact, not a runtime
    // default (ADR-0020).
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
}
