using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Hotkey;

/// <summary>
/// The window's global-hotkey registration owner (the <see cref="HotkeyApi"/>
/// delegate bag over the RegisterHotKey surface): the pass entry (the
/// <see cref="RefreshPass"/> resolve → duplicate-log → diff sequence), the
/// id allocation, the register/unregister diff against the OS on every
/// idempotent refresh, and the WM_HOTKEY routing to the owning widget's fire
/// path. A refresh is a no-op for the cells it already holds (the ids are
/// stable across refreshes, so the OS state is not churned); a changed set
/// unregisters the removed cells and registers the added ones. A cell the OS
/// refuses (owned by another program) stays untracked - that widget's
/// hotkey is inert, tapping still works - and logs one line (once per cell
/// per session), the same per-session dedup the pass applies to the
/// profile-order duplicate conflicts. Every member runs on the window's UI
/// thread (the context seam marshals off-thread calls to the dispatcher),
/// so the dictionaries stay unsynchronized by design.
/// </summary>
internal sealed class GlobalHotkeyManager(HotkeyApi api, DiagLog log) : IDisposable
{
    private const int FirstId = 0x574D0001; // "WM" + counter

    /// <summary>Win32 MOD_NOREPEAT: the OS must not autorepeat a global
    /// hotkey. Added at this OS boundary, so the chord policy's flags stay
    /// the pure modifier vocabulary; the window's tests pin the value
    /// (0x4000) on both sides of the seam.</summary>
    private const int ModNoRepeat = 0x4000;

    private readonly Dictionary<(int Flags, ushort Vk), int> _idByCell = new();
    private readonly Dictionary<int, (ushort Vk, IGlobalHotkeyProvider Owner)> _registered = new();
    private readonly HashSet<(int Flags, ushort Vk)> _foreignLogged = new();
    // The duplicate-chord conflicts already logged this session (the
    // foreign-logged mirror for the profile-order conflicts, which the OS
    // never sees): one line per conflict per session, not one per pass.
    private readonly HashSet<(int Flags, ushort Vk)> _duplicateLogged = new();
    private IntPtr _handle;
    private int _nextId = FirstId;

    /// <summary>
    /// The idempotent global-hotkey registration pass (ADR-0019): resolves
    /// the desired set from the profile-order candidates (the kill-switch
    /// veto, the first-in-profile-order-wins duplicate rule), logs one line
    /// per duplicate cell per session (the tripped kill switch's drops are
    /// not duplicates, so nothing is logged while it is tripped), and runs
    /// the register/unregister diff. The window's trigger keeps only the
    /// handle and the candidate/kill-switch reads.
    /// </summary>
    public void RefreshPass(IntPtr handle, IReadOnlyList<DesiredGlobalHotkey> candidates, bool killSwitchTripped)
    {
        var (desired, dropped) = GlobalHotkeyPolicy.ResolveDesired(killSwitchTripped, candidates);
        if (!killSwitchTripped)
        {
            foreach (DesiredGlobalHotkey duplicate in dropped)
            {
                if (!_duplicateLogged.Add((duplicate.ModFlags, duplicate.VirtualKey)))
                    continue;
                log.Write(() => $"Global hotkey {duplicate.Chord} is claimed by an earlier widget; the later one stays tap-only");
            }
        }
        Refresh(handle, desired);
    }

    /// <summary>
    /// The idempotent refresh pass: diffs the desired set against the current
    /// registrations on <paramref name="handle"/> (a zero handle clears
    /// everything - the window's handle is only valid while it is alive).
    /// The MOD_NOREPEAT flag is added at this OS boundary, so the policy's
    /// flags stay the pure modifier vocabulary.
    /// </summary>
    public void Refresh(IntPtr handle, IReadOnlyList<DesiredGlobalHotkey> desired)
    {
        if (handle == IntPtr.Zero)
        {
            Clear();
            return;
        }
        _handle = handle;

        var desiredCells = new HashSet<(int Flags, ushort Vk)>(desired.Count);
        foreach (DesiredGlobalHotkey hotkey in desired)
            desiredCells.Add((hotkey.ModFlags, hotkey.VirtualKey));

        // Unregister the cells that left the desired set.
        foreach ((var cell, int id) in _idByCell.ToList())
        {
            if (desiredCells.Contains(cell)) continue;
            if (_registered.Remove(id))
                api.UnregisterHotKey(handle, id, cell.Vk);
            _idByCell.Remove(cell);
        }

        // Add the cells that joined; a held cell whose owner instance was replaced (a profile import rehydrated the widget) is released and re-registered onto the new owner.
        foreach (DesiredGlobalHotkey hotkey in desired)
        {
            var cell = (Flags: hotkey.ModFlags, Vk: hotkey.VirtualKey);
            if (_idByCell.TryGetValue(cell, out int heldId))
            {
                if (ReferenceEquals(_registered.GetValueOrDefault(heldId).Owner, hotkey.Owner))
                    continue;
                api.UnregisterHotKey(handle, heldId, cell.Vk);
                _idByCell.Remove(cell);
                _registered.Remove(heldId);
            }
            int id = _nextId++;
            if (api.RegisterHotKey(handle, id, hotkey.ModFlags | ModNoRepeat, hotkey.VirtualKey))
            {
                _idByCell[cell] = id;
                _registered[id] = (hotkey.VirtualKey, hotkey.Owner);
            }
            else if (_foreignLogged.Add(cell))
            {
                log.Write(() =>
                    $"Global hotkey {hotkey.Chord} is owned by another program; this widget's hotkey is inert (tapping still works)");
            }
        }
    }

    /// <summary>
    /// Routes a WM_HOTKEY id (the message's wParam) to the owning widget's
    /// fire path; false for an unknown id (a refusal or an unregistered
    /// chord), so the message keeps flowing to the default handler.
    /// </summary>
    public bool Fire(int id)
    {
        if (!_registered.TryGetValue(id, out (ushort, IGlobalHotkeyProvider Owner) entry)) return false;
        entry.Owner.FireGlobalHotkey();
        return true;
    }

    /// <summary>Releases every registration on the last known handle (the teardown step).</summary>
    public void Dispose()
    {
        Clear();
    }

    private void Clear()
    {
        if (_handle == IntPtr.Zero)
        {
            _idByCell.Clear();
            _registered.Clear();
            return;
        }
        foreach ((var cell, int id) in _idByCell.ToList())
            api.UnregisterHotKey(_handle, id, cell.Vk);
        _idByCell.Clear();
        _registered.Clear();
    }
}
