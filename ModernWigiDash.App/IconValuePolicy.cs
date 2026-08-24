using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// The icon value policy for the inspector's icon properties: whether a value
/// names a bundled Griddy icon or a custom SVG file path, the read precedence
/// when both companion properties hold a value, and the write-back mutual
/// exclusion. A pure module: the picker dialog's chrome
/// (<see cref="DialogHost.ShowIconPicker"/>) and the inspector's write-back
/// routing (<see cref="Inspector.InspectorController"/>) decide through it,
/// and tests drive the same seam.
/// </summary>
public static class IconValuePolicy
{
    /// <summary>True when the value names a bundled Griddy icon (non-blank and in the catalog).</summary>
    public static bool IsNamed(string? value) => GriddyIcons.Contains(value ?? "");

    /// <summary>True when the value is a custom icon: non-blank and not a catalog name.</summary>
    public static bool IsCustom(string? value) => !string.IsNullOrWhiteSpace(value) && !IsNamed(value);

    /// <summary>
    /// The read precedence: the icon file path wins over the named icon when
    /// both companion properties hold a value; blank or null degrades to the
    /// other, and neither degrades to the empty string.
    /// </summary>
    public static string ResolveCurrent(string? named, string? iconFile)
        => !string.IsNullOrWhiteSpace(iconFile) ? iconFile : named ?? "";

    /// <summary>
    /// The write-back mutual exclusion: exactly one companion property holds
    /// the chosen value. A named selection clears the icon file; a custom path
    /// clears the named icon.
    /// </summary>
    public static (string Named, string IconFile) SplitWriteback(string chosen)
        => IsNamed(chosen) ? (chosen, "") : ("", chosen);
}
