namespace ModernWigiDash.Widgets.Steam.Models;

/// <summary>
/// The community-API <c>personastate</c> ordinal's ONE fact: its display
/// label and its online-dot color. The two facets live in a single table so
/// they cannot drift apart (a new Steam state is one row, not an edit to a
/// label array in one type and a color switch in another). Unknown ordinals
/// read as the ordinal string with the offline dot.
/// </summary>
internal static class SteamPersonaState
{
    /// <summary>The ordinal → (label, online-dot color) table (Steam's
    /// <c>personastate</c> ordinals 0–5).</summary>
    private static readonly (string Label, SKColor Color)[] Table =
    [
        ("Offline", new SKColor(144, 148, 153, 255)),
        ("Online", new SKColor(86, 199, 115, 255)),
        ("InGame", new SKColor(95, 177, 255, 255)),
        ("Busy", new SKColor(255, 107, 107, 255)),
        ("Away", new SKColor(255, 197, 91, 255)),
        ("Snoozing", new SKColor(255, 197, 91, 255)),
    ];

    /// <summary>The offline dot: the fallback for any ordinal the table does not name.</summary>
    private static readonly SKColor FallbackColor = new(144, 148, 153, 255);

    /// <summary>
    /// The state's display label: the missing-state sentinel (-1, the client's
    /// default for an absent or unparseable <c>personastate</c>) reads as
    /// Offline, a known ordinal reads as its name, and any other unknown
    /// ordinal reads as the ordinal itself (a new Steam state is never a
    /// silent blank).
    /// </summary>
    public static string Label(int personState)
    {
        if (personState == -1)
            return "Offline";

        if (personState >= 0 && personState < Table.Length)
            return Table[personState].Label;

        return personState.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The online-dot color (unknown ordinals read as the offline dot).</summary>
    public static SKColor Color(int personState)
        => personState >= 0 && personState < Table.Length
            ? Table[personState].Color
            : FallbackColor;
}
