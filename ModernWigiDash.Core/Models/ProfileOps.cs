using System.Reflection;
using System.Text.Json;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Models;

/// <summary>
/// Profile operations: page CRUD, widget placement/rehydration, and JSON
/// export/import. The pure-model mutations the window used to own — the
/// window keeps only dialogs, selection, and refresh. Widgets are rehydrated
/// through the loader + host context, so a profile round-trips Export→Import
/// with its custom property values intact.
/// </summary>
public static class ProfileOps
{
    // ── page CRUD ───────────────────────────────────────────

    /// <summary>Adds a page and activates it. Returns the new page.</summary>
    public static PageLayout AddPage(ProfileLayout profile, string? name = null)
    {
        var page = new PageLayout { PageName = name ?? $"Page {profile.Pages.Count + 1}" };
        profile.Pages.Add(page);
        profile.ActivePageIndex = profile.Pages.Count - 1;
        return page;
    }

    /// <summary>
    /// Deletes the page at <paramref name="index"/>. Refuses when it is the
    /// last page. Returns true when deleted.
    /// </summary>
    public static bool DeletePage(ProfileLayout profile, int index)
    {
        if (index < 0 || index >= profile.Pages.Count || profile.Pages.Count <= 1) return false;
        foreach (var placed in profile.Pages[index].Widgets)
        {
            DisposeWidgetInstance(placed);
        }
        profile.Pages.RemoveAt(index);
        if (profile.ActivePageIndex >= profile.Pages.Count)
        {
            profile.ActivePageIndex = profile.Pages.Count - 1;
        }
        return true;
    }

    /// <summary>Renames a page (no-op on null/blank names).</summary>
    public static void RenamePage(PageLayout page, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        page.PageName = name;
    }

    /// <summary>Clears every widget from the page, disposing active instances.</summary>
    public static void ClearPage(PageLayout page)
    {
        foreach (var placed in page.Widgets)
        {
            DisposeWidgetInstance(placed);
        }
        page.Widgets.Clear();
    }

    /// <summary>
    /// Disposes every active widget instance across all pages (timers, sockets,
    /// and subscriptions must not outlive the profile they belong to). The page
    /// structure is left intact — used before replacing a profile wholesale,
    /// e.g. after an import.
    /// </summary>
    public static void DisposeProfile(ProfileLayout profile)
    {
        foreach (var page in profile.Pages)
        {
            foreach (var placed in page.Widgets)
            {
                DisposeWidgetInstance(placed);
            }
        }
    }

    // ── placement & rehydration ─────────────────────────────

    /// <summary>
    /// Creates a placed widget on the active page: looks up the display name,
    /// rehydrates the instance, sizes it (explicit or the widget's default),
    /// and assigns the next ZIndex. Returns null when the plugin is unknown.
    /// </summary>
    public static PlacedWidgetInstance? PlaceWidget(
        ProfileLayout profile,
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        string pluginId,
        float x,
        float y,
        float width = -1,
        float height = -1)
    {
        var placed = new PlacedWidgetInstance
        {
            PluginId = pluginId,
            DisplayName = loader.RegisteredPlugins.FirstOrDefault(p => p.PluginId == pluginId)?.DisplayName ?? pluginId,
            X = x,
            Y = y,
            ZIndex = profile.ActivePage.Widgets.Count + 1
        };

        var instance = RehydrateWidget(loader, context, placed);
        if (instance == null) return null;

        placed.Width = width > 0 ? width : instance.DefaultSize.Width;
        placed.Height = height > 0 ? height : instance.DefaultSize.Height;

        profile.ActivePage.Widgets.Add(placed);
        return placed;
    }

    /// <summary>
    /// Creates and initializes the active widget instance for a placed widget,
    /// then applies the user-configured custom property values (surviving
    /// Export/Import round-trips).
    /// </summary>
    public static IModernWidget? RehydrateWidget(
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        PlacedWidgetInstance placed)
    {
        var instance = loader.CreateInstance(placed.PluginId);
        if (instance == null) return null;

#pragma warning disable S6966 // Widget initialization must complete before placement — sync wrapper during startup
        instance.InitializeAsync(context).GetAwaiter().GetResult();
#pragma warning restore S6966

        var type = instance.GetType();
        foreach (var prop in type.GetProperties())
        {
            var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
            if (attr == null) continue;
            if (!placed.PropertyValues.TryGetValue(prop.Name, out object? raw)) continue;

            object? value = ConvertPropertyValue(raw, prop.PropertyType);
            if (value == null) continue;

            try
            {
                prop.SetValue(instance, value);
                instance.OnPropertyChanged(prop.Name, value);
            }
            catch
            {
                // Stored value is incompatible with the widget property type; ignore it
                System.Diagnostics.Debug.WriteLine("Stored value incompatible with widget property type (ignored)");
            }
        }

        DisposeWidgetInstance(placed);
        placed.ActiveInstance = instance;
        return instance;
    }

    /// <summary>
    /// Tears down a placed widget's active instance. Widget teardown (timers,
    /// sockets, subscriptions) must never break profile operations, so failures
    /// are swallowed. ProfileOps is a synchronous module (ADR-0001), so async
    /// disposal is awaited synchronously.
    /// </summary>
    private static void DisposeWidgetInstance(PlacedWidgetInstance? placed)
    {
        if (placed?.ActiveInstance == null) return;
        try
        {
            placed.ActiveInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Widget teardown must not break profile operations.
        }
        placed.ActiveInstance = null;
    }

    /// <summary>
    /// Imported JSON dictionaries arrive as JsonElement values; deserialize
    /// them into the real type.
    /// </summary>
    public static object? ConvertPropertyValue(object? raw, Type targetType)
    {
        if (raw is not JsonElement je) return raw;
        try
        {
            return je.Deserialize(targetType);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── export / import ─────────────────────────────────────

    /// <summary>Serializes the profile to JSON.</summary>
    public static string ExportJson(ProfileLayout profile)
        => JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Max widgets a page may carry after an import (startup DoS cap).</summary>
    private const int MaxWidgetsPerPage = 200;

    /// <summary>Max pages after an import (total-size DoS cap).</summary>
    private const int MaxPagesPerProfile = 50;

    /// <summary>Max widgets across the whole imported profile.</summary>
    private const int MaxTotalWidgets = 1000;

    /// <summary>
    /// Deserializes a profile and rehydrates every placed widget so the loaded
    /// profile is immediately renderable. Returns null on parse failure.
    ///
    /// Imported profiles are UNTRUSTED input: before rehydration the profile is
    /// sanitized — widget count is capped, action commands (process/keystroke
    /// launch) are cleared, and image/icon paths are restricted to safe
    /// relative paths.
    /// </summary>
    public static ProfileLayout? ImportJson(string json, WidgetPluginLoader loader, IModernWigiDashContext context)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<ProfileLayout>(json);
            if (loaded == null) return null;

            SanitizeImportedProfile(loaded);

            foreach (var page in loaded.Pages)
            {
                foreach (var placed in page.Widgets)
                {
                    RehydrateWidget(loader, context, placed);
                }
            }
            return loaded;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the untrusted-import rules: page widget caps, background path
    /// sanitization, and clearing action-command values (the Hotkey widget's
    /// Launch/URL/keystroke execution must be re-entered by the user after
    /// importing a foreign profile).
    /// </summary>
    private static void SanitizeImportedProfile(ProfileLayout profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ActivePage?.BackgroundImagePath))
        {
            profile.ActivePage.BackgroundImagePath = SafeRelativePath(profile.ActivePage.BackgroundImagePath);
        }

        if (profile.Pages.Count > MaxPagesPerProfile)
        {
            profile.Pages = profile.Pages.Take(MaxPagesPerProfile).ToList();
        }

        // Enforce per-page and total widget caps in one pass: later pages are
        // emptied once the total budget is exhausted.
        int total = 0;
        foreach (var page in profile.Pages)
        {
            if (!string.IsNullOrWhiteSpace(page.BackgroundImagePath))
            {
                page.BackgroundImagePath = SafeRelativePath(page.BackgroundImagePath);
            }

            int remaining = MaxTotalWidgets - total;
            if (remaining <= 0)
            {
                page.Widgets.Clear();
                continue;
            }

            int allowed = Math.Min(page.Widgets.Count, Math.Min(MaxWidgetsPerPage, remaining));
            for (int i = 0; i < allowed; i++)
            {
                SanitizeWidgetValues(page.Widgets[i]);
            }
            if (page.Widgets.Count > allowed)
            {
                page.Widgets = page.Widgets.Take(allowed).ToList();
            }
            total += allowed;
        }
    }

    private static void SanitizeWidgetValues(PlacedWidgetInstance placed)
    {
        // ActionCommand drives Process.Start / SendInput on the Hotkey widget
        // (identified by its property name, widget-agnostically): a foreign
        // profile must not silently arm command execution. Cleared whenever
        // present — ActionType is NOT required (it defaults to "Launch App",
        // so omitting it in the profile is a valid bypass). ActionCommand is
        // also Path-typed and listed in PathPropertyKeys, but this unconditional
        // clear must run FIRST: the path rules alone would preserve a safe
        // relative command. PropertyValues hold JsonElement after
        // deserialization — normalize before inspecting.
        if (placed.PropertyValues.TryGetValue("ActionCommand", out var raw) &&
            ConvertPropertyValue(raw, typeof(string)) is string command &&
            !string.IsNullOrWhiteSpace(command))
        {
            placed.PropertyValues["ActionCommand"] = "";
        }

        // Image/icon paths: rooted, UNC, or ..\ paths would read arbitrary
        // local files; restrict imports to safe relative paths.
        foreach (var key in PathPropertyKeys)
        {
            if (placed.PropertyValues.TryGetValue(key, out var value) &&
                ConvertPropertyValue(value, typeof(string)) is string path)
            {
                placed.PropertyValues[key] = SafeRelativePath(path);
            }
        }
    }

    /// <summary>
    /// Widget property names that carry filesystem paths. Must match the
    /// widgets' <see cref="WidgetPropertyAttribute"/> declarations exactly —
    /// ProfileSanitizerDriftTests reflects over the Widgets assembly and asserts
    /// this set equals every <see cref="WidgetPropertyType.Path"/> property, so
    /// a renamed path property fails the build instead of silently disarming
    /// the import guard.
    /// </summary>
    internal static readonly string[] PathPropertyKeys =
        ["ActionCommand", "IconFile", "ImagePath"];

    private static string SafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return "";
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "";
        if (path.Split('\\', '/').Any(segment => segment == "..")) return "";
        return path;
    }
}
