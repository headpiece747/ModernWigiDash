using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Theming;

/// <summary>
/// The live-theme owner (candidate 2 of the architecture review): one module
/// that owns the current <see cref="ThemeSettings"/> value, its load + legacy
/// migration, and its persistence. Before this module, the active theme was a
/// mutable static (<c>ThemeSettings.Theme</c>) with ~28 cross-file read/write
/// sites and no owner - correctness rested on WPF's single-thread discipline
/// plus remembering to call the apply step after every write. Now the value has
/// one owner: writers replace it through <see cref="Replace"/>, readers read
/// <see cref="Current"/>, and the apply step rides the change (the caller routes
/// through <see cref="ThemeApplicator.Apply"/>), so a forgotten-apply is a
/// consequence of the seam, not a separate remembered call.
/// </summary>
internal static class ThemeStore
{
    /// <summary>The path the production store persists to (ADR-0021 user state
    /// dir). Tests inject a temp path through <see cref="PersistedPath"/>.</summary>
    internal static string? PersistedPath { get; set; } = ThemeSettings.DefaultPath();

    /// <summary>The current live theme. Lazily loaded from disk on first access
    /// (the load + one-time legacy migration run once here, in the App layer,
    /// so the Core model stays a pure serializable value).</summary>
    public static ThemeSettings Current
    {
        get => _current ??= LoadProduction();
        private set => _current = value;
    }

    private static ThemeSettings? _current;

    /// <summary>Replaces the live theme (the one write entry point). Callers
    /// persist via <see cref="SaveCurrent"/> and re-apply via
    /// <see cref="ThemeApplicator.Apply"/>; this method only swaps the value.</summary>
    public static void Replace(ThemeSettings theme)
        => Current = theme;

    /// <summary>Persists the current theme to the persisted path (creating the
    /// directory when missing). Returns false on a write failure so the caller
    /// can surface it ("applies for this session only").</summary>
    public static bool SaveCurrent()
        => ThemeSettings.Save(Current, PersistedPath!);

    /// <summary>Loads the production theme (state dir + legacy migration +
    /// FileLog). The one place the load side-effect lives.</summary>
    private static ThemeSettings LoadProduction()
        => ThemeSettings.Load();
}
