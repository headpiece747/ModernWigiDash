namespace ModernWigiDash.Sdk;

/// <summary>
/// PM_PRESENT_MODE value → label mapping (PresentMonAPI.h). The id is the
/// PresentMon enum value as polled from the dynamic query; the widget stores
/// the id in the snapshot and derives both display forms from this single
/// mapping site. "-1" (no data) and unknown ids render as "—".
/// </summary>
public static class PresentMonPresentMode
{
    public static string FullName(int id) => id switch
    {
        0 => "Unknown",
        1 => "Hardware Legacy Flip",
        2 => "Hardware Legacy Copy to Front Buffer",
        3 => "Hardware Independent Flip",
        4 => "Composed Flip",
        5 => "Composed Copy with GPU GDI",
        6 => "Composed Copy with CPU GDI",
        8 => "Hardware Composed: Independent Flip",
        _ => "—",
    };

    public static string ShortName(int id) => id switch
    {
        0 => "Unknown",
        1 => "HW Legacy Flip",
        2 => "HW Copy to Front",
        3 => "HW Ind. Flip",
        4 => "Composed Flip",
        5 => "Comp. Copy (GPU)",
        6 => "Comp. Copy (CPU)",
        8 => "HWC Ind. Flip",
        _ => "—",
    };
}
