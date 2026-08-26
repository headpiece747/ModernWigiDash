using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Hotkey;

/// <summary>
/// One desired global-hotkey registration: the RegisterHotKey operands
/// (the GlobalHotkeyChordPolicy MOD vocabulary + the main key's virtual-key
/// code), the chord's stored spelling (the duplicate's log line), and the
/// owning widget (the WM_HOTKEY routing target).
/// </summary>
internal sealed record DesiredGlobalHotkey(int ModFlags, ushort VirtualKey, string Chord, IGlobalHotkeyProvider Owner);

/// <summary>
/// The desired-registration resolution (pure): the profile's provider
/// widgets in profile order, the kill-switch veto, and the duplicate rule
/// (the first widget in profile order wins a (flags, key) cell; a later
/// duplicate is dropped so the window can log one line for it). A tripped
/// kill switch drops every candidate - nothing is registered and the dropped
/// list is the full candidate set, which the window must NOT log as
/// duplicates.
/// </summary>
internal static class GlobalHotkeyPolicy
{
    /// <summary>
    /// Resolves the desired registration set from the profile-order
    /// candidates. Returns the (desired, dropped) split; <paramref
    /// name="killSwitchTripped"/> true drops every candidate (the
    /// anti-cheat off switch: no global-hotkey registration while tripped).
    /// </summary>
    public static (IReadOnlyList<DesiredGlobalHotkey> Desired, IReadOnlyList<DesiredGlobalHotkey> Dropped) ResolveDesired(
        bool killSwitchTripped,
        IEnumerable<DesiredGlobalHotkey> candidates)
    {
        List<DesiredGlobalHotkey> all = candidates.ToList();
        if (killSwitchTripped)
            return ([], all);

        var seen = new HashSet<(int Flags, ushort Vk)>();
        var desired = new List<DesiredGlobalHotkey>();
        var dropped = new List<DesiredGlobalHotkey>();
        foreach (DesiredGlobalHotkey candidate in all)
        {
            if (seen.Add((candidate.ModFlags, candidate.VirtualKey)))
                desired.Add(candidate);
            else
                dropped.Add(candidate);
        }
        return (desired, dropped);
    }
}
