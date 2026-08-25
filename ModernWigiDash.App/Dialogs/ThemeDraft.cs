using System.Reflection;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Dialogs;

/// <summary>
/// The theme dialog's draft module: the per-property entries seeded from the
/// active theme, the single validity verdict, apply-to-settings, and
/// reset-to-defaults. Pure: the dialog (<see cref="ThemeDialog"/>) builds the
/// editors and forwards to it, and tests drive the draft without a window.
/// The entries hold the <see cref="PropertyInfo"/> references, so apply and
/// reset never re-resolve a property by name.
/// </summary>
internal sealed class ThemeDraft
{
    /// <summary>One themeable property in display order, with its display copy
    /// and the draft's current hex value.</summary>
    internal sealed class Entry
    {
        public Entry(string name, PropertyInfo property, string group, string friendlyName, string description, string hex)
        {
            Name = name;
            Property = property;
            Group = group;
            FriendlyName = friendlyName;
            Description = description;
            Hex = hex;
        }

        public string Name { get; }

        public PropertyInfo Property { get; }

        public string Group { get; }

        public string FriendlyName { get; }

        public string Description { get; }

        public string Hex { get; set; }
    }

    private readonly List<Entry> _entries;

    public ThemeDraft()
    {
        var theme = ThemeSettings.Theme;
        _entries = ThemeSettings.StringProperties
            .Select(p => new Entry(
                p.Name,
                p,
                GroupFor(p.Name),
                ThemePresentation.FriendlyName(p.Name),
                DescriptionFor(p.Name),
                (string?)p.GetValue(theme) ?? "#000000"))
            .OrderBy(e => e.Group, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The entries in display order: group (then name), so the dialog
    /// renders them without re-deriving the order.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>
    /// The single validity verdict: the first entry whose hex does not parse,
    /// or null when every entry is valid.
    /// </summary>
    public string? InvalidEntryName => _entries.FirstOrDefault(e => ThemeSettings.ParseColor(e.Hex) is null)?.Name;

    /// <summary>True when every entry holds a parseable hex value.</summary>
    public bool IsValid => InvalidEntryName is null;

    /// <summary>
    /// Applies the draft to the active theme: only parseable values are
    /// written, so an invalid entry can never corrupt the theme.
    /// </summary>
    public void ApplyToSettings()
    {
        var theme = ThemeSettings.Theme;
#pragma warning disable S3267 // one write per parseable entry (a conditional SetValue), not a LINQ filter
        foreach (var entry in _entries)
        {
            if (ThemeSettings.ParseColor(entry.Hex) is not null)
                entry.Property.SetValue(theme, entry.Hex);
        }
#pragma warning restore S3267
    }

    /// <summary>Restores every entry to the built-in defaults (a fresh
    /// <see cref="ThemeSettings"/>'s values).</summary>
    public void ResetToDefaults()
    {
        var defaults = new ThemeSettings();
        foreach (var entry in _entries)
            entry.Hex = (string?)entry.Property.GetValue(defaults) ?? "#000000";
    }

    /// <summary>Routes an editor's live hex change into the draft (the dialog's
    /// one update site).</summary>
    public void UpdateHex(string name, string hex)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown theme property: {name}", nameof(name));
        entry.Hex = hex.Trim();
    }

    /// <summary>The draft's current hex value for a property (the dialog's
    /// reset sync reads through it).</summary>
    public string HexFor(string name)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown theme property: {name}", nameof(name));
        return entry.Hex;
    }

    private static string GroupFor(string name)
        => ThemePresentation.Groups.TryGetValue(name, out var group) ? group : "Other";

    private static string DescriptionFor(string name)
        => ThemePresentation.Descriptions.TryGetValue(name, out var description) ? description : "";
}
