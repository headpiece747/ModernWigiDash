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
    /// <summary>
    /// Finds the placed instance wrapping a live widget instance (by
    /// reference) — the single identity scan the window's PersistProperty and
    /// the test context share.
    /// </summary>
    public static PlacedWidgetInstance? FindPlacedWidget(ProfileLayout profile, object widgetInstance)
        => profile.Pages
            .SelectMany(page => page.Widgets)
            .FirstOrDefault(placed => ReferenceEquals(placed.ActiveInstance, widgetInstance));

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
    /// The single delete-page rule: the last page can never be deleted. Owned
    /// here with <see cref="DeletePage"/> so the UI gate and the operation
    /// share one predicate — the tab strip consults this, it never re-derives
    /// the rule.
    /// </summary>
    public static bool CanDeletePage(ProfileLayout profile) => profile.Pages.Count > 1;

    /// <summary>
    /// The single bounds-safe page read: the page at <paramref name="index"/>
    /// or null when the index is stale. Callers that need a page's facts
    /// before an operation (the delete confirm's name/count) read through
    /// this instead of indexing <see cref="ProfileLayout.Pages"/> themselves,
    /// so a stale index degrades to a no-op rather than throwing in the
    /// window ahead of the module's own validation.
    /// </summary>
    public static PageLayout? TryGetPage(ProfileLayout profile, int index)
        => index >= 0 && index < profile.Pages.Count ? profile.Pages[index] : null;

    /// <summary>
    /// Deletes the page at <paramref name="index"/>. Refuses when it is the
    /// last page. Returns true when deleted.
    /// </summary>
    public static bool DeletePage(ProfileLayout profile, int index)
    {
        if (index < 0 || index >= profile.Pages.Count || !CanDeletePage(profile)) return false;
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

    /// <summary>
    /// Switches the profile's active page. Returns false when the index is out
    /// of range — the single place page navigation validates, so callers (the
    /// window, the input controller's navigation seam) never index a missing
    /// page.
    /// </summary>
    public static bool SetActivePageIndex(ProfileLayout profile, int index)
    {
        if (index < 0 || index >= profile.Pages.Count) return false;
        profile.ActivePageIndex = index;
        return true;
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
    /// Removes one placed widget from its page, disposing its active instance —
    /// the single-widget teardown path, the counterpart of <see cref="ClearPage"/>
    /// and <see cref="DeletePage"/>. Returns true when the widget was found and
    /// removed; a widget not present on the page is left untouched.
    /// </summary>
    public static bool RemoveWidget(PageLayout page, PlacedWidgetInstance placed)
    {
        if (page is null || placed is null || !page.Widgets.Remove(placed)) return false;
        DisposeWidgetInstance(placed);
        return true;
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

    /// <summary>Max bytes an import file may carry — the file-read counterpart
    /// of the sanitizer caps: far beyond any real exported profile, anything
    /// larger is untrusted junk and must be rejected before any parsing.</summary>
    public const long MaxImportFileBytes = 10 * 1024 * 1024;

    /// <summary>True when an import file exceeds <see cref="MaxImportFileBytes"/> —
    /// the window's pre-read reject rule, so the guard's boundary lives in one
    /// place with the cap it enforces.</summary>
    public static bool IsImportFileTooLarge(long fileLength) => fileLength > MaxImportFileBytes;

    /// <summary>
    /// Replaces the active profile with an imported one: disposes every widget
    /// instance of <paramref name="current"/> and returns
    /// <paramref name="imported"/> as the new active profile. The swap is one
    /// site, so there is no in-between state where two profiles own live
    /// widgets (the window used to hand-roll dispose-then-assign).
    /// </summary>
    public static ProfileLayout ReplaceProfile(ProfileLayout current, ProfileLayout imported)
    {
        DisposeProfile(current);
        return imported;
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
            DisplayName = loader.RegisteredPlugins.FirstOrDefault(p => string.Equals(p.PluginId, pluginId, StringComparison.Ordinal))?.DisplayName ?? pluginId,
            X = x,
            Y = y,
            ZIndex = profile.ActivePage.Widgets.Count + 1
        };

        var instance = RehydrateWidget(loader, context, placed);
        if (instance is null) return null;

        placed.Width = width > 0 ? width : instance.DefaultSize.Width;
        placed.Height = height > 0 ? height : instance.DefaultSize.Height;

        profile.ActivePage.Widgets.Add(placed);
        return placed;
    }

    /// <summary>
    /// Places a widget at its natural position: full-screen widgets (the
    /// catalog's nominal default nearly fills the framebuffer) at the origin,
    /// smaller ones centered on the grid. The center is rounded to the
    /// snap-to-grid cells; the widget itself is not re-snapped (a 2-cell
    /// widget cannot be both centered and cell-aligned — centering wins, as it
    /// did when the window owned this math). The size comes from the catalog
    /// entry's [WidgetMetadata] fact, so no probe instance is constructed —
    /// the only instance this method creates is the real one, inside
    /// <see cref="PlaceWidget"/>.
    /// </summary>
    public static PlacedWidgetInstance? PlaceCentered(
        ProfileLayout profile,
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        string pluginId)
    {
        if (loader.FindPlugin(pluginId) is not { } info) return null;

        if (info.DefaultSize.Width >= DisplayGeometry.FramebufferWidth - 10 || info.DefaultSize.Height >= DisplayGeometry.FramebufferHeight - 10)
        {
            return PlaceWidget(profile, loader, context, pluginId, 0, 0);
        }

        float cx = GridSizeExtensions.SnapX(DisplayGeometry.FramebufferWidth / 2.0f);
        float cy = GridSizeExtensions.SnapY(DisplayGeometry.FramebufferHeight / 2.0f);
        return PlaceWidget(profile, loader, context, pluginId, cx - info.DefaultSize.Width / 2, cy - info.DefaultSize.Height / 2);
    }

    /// <summary>
    /// Creates and initializes the active widget instance for a placed widget,
    /// then applies the user-configured custom property values (surviving
    /// Export/Import round-trips). Failures are contained per widget: one
    /// throwing widget (broken constructor or InitializeAsync) is logged and
    /// skipped so it cannot abort the whole import.
    /// </summary>
    public static IModernWidget? RehydrateWidget(
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        PlacedWidgetInstance placed)
    {
        IModernWidget? instance = null;
        try
        {
            WidgetCreateResult created = loader.CreateInstanceResult(placed.PluginId);
            if (created is WidgetCreateResult.Broken broken)
            {
                context.LogError($"Widget '{placed.PluginId}' is broken and was skipped: {broken.Reason}");
                return null;
            }
            if (created is not WidgetCreateResult.Ok ok) return null;
            instance = ok.Widget;

            // The instance and the placed widget share one identity: the
            // placed's InstanceId survives Export/Import, so rehydration must
            // sync it back onto the fresh instance (widgets key caches by it).
            instance.InstanceId = placed.InstanceId;

#pragma warning disable S6966 // Widget initialization must complete before placement — sync wrapper during startup
            instance.InitializeAsync(context).GetAwaiter().GetResult();
#pragma warning restore S6966

            var type = instance.GetType();
            foreach (var prop in type.GetProperties())
            {
                var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
                if (attr is null) continue;
                if (!placed.PropertyValues.TryGetValue(prop.Name, out object? raw)) continue;

                object? value = ConvertPropertyValue(raw, prop.PropertyType);
                if (value is null) continue;

                try
                {
                    prop.SetValue(instance, value);
                    instance.OnPropertyChanged(prop.Name, value);
                }
                catch
                {
                    // Stored value is incompatible with the widget property type; ignore it
                    context.LogError($"Stored value incompatible with widget property '{prop.Name}' on '{placed.PluginId}' (ignored)");
                }
            }

            DisposeWidgetInstance(placed);
            placed.ActiveInstance = instance;
            return instance;
        }
        catch (Exception ex)
        {
            context.LogError($"Widget rehydration failed for '{placed.PluginId}'; the widget is skipped.", ex);
            if (instance is not null && !ReferenceEquals(instance, placed.ActiveInstance))
            {
                try
                {
                    instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Teardown of the failed instance must not mask the original error.
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Tears down a placed widget's active instance. Widget teardown (timers,
    /// sockets, subscriptions) must never break profile operations, so failures
    /// are swallowed. ProfileOps is a synchronous module (ADR-0001), so async
    /// disposal is awaited synchronously.
    /// </summary>
    private static void DisposeWidgetInstance(PlacedWidgetInstance? placed)
    {
        if (placed?.ActiveInstance is null) return;
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

    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    /// <summary>Serializes the profile to JSON.</summary>
    public static string ExportJson(ProfileLayout profile)
        => JsonSerializer.Serialize(profile, ExportOptions);

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
    public static ProfileLayout? ImportJson(
        string json,
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        bool sanitize = true)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<ProfileLayout>(json);
            if (loaded is null) return null;

            // The app's own persisted profile is TRUSTED input: skipping the
            // untrusted-import sanitizer preserves the user's configured
            // ActionCommand / ImagePath / BackgroundImagePath values (the
            // sanitizer's SafeRelativePath and ActionCommand clear would wipe
            // them on every restart). Manual imports stay sanitized (default).
            if (sanitize)
            {
                SanitizeImportedProfile(loaded);
            }

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
        // Untrusted JSON may carry null collections ("pages": null etc.) —
        // repair them before any counting, or the sanitizer NREs on exactly
        // the input shape it exists for. Null PAGE ELEMENTS ("pages":[null])
        // are dropped the same way.
        profile.Pages ??= [];
        profile.Pages = profile.Pages.Where(p => p is not null).ToList();

        // A profile with zero pages cannot exist at runtime (the ctor creates
        // one, DeletePage refuses the last) — an imported JSON with an empty
        // pages array must be repaired here, or ActivePage hands out an orphan
        // page that is not part of the profile.
        if (profile.Pages.Count == 0)
        {
            profile.Pages.Add(new PageLayout());
        }

        // Null-element filtering may have shrunk the page list — re-clamp the
        // deserialized active index (its setter clamped against the ORIGINAL
        // count, so it can still point past the repaired list).
        profile.ActivePageIndex = Math.Min(profile.ActivePageIndex, profile.Pages.Count - 1);

        if (!string.IsNullOrWhiteSpace(profile.ActivePage?.BackgroundImagePath))
        {
            profile.ActivePage.BackgroundImagePath = SafeRelativePath(profile.ActivePage.BackgroundImagePath);
        }

        if (profile.Pages.Count > MaxPagesPerProfile)
        {
            profile.Pages = profile.Pages.Take(MaxPagesPerProfile).ToList();
            // Truncation leaves the deserialized active index past the new
            // last page — re-clamp it, or swipe navigation targets a missing
            // page (InputController refuses out-of-range switches).
            profile.ActivePageIndex = Math.Min(profile.ActivePageIndex, profile.Pages.Count - 1);
        }

        // Enforce per-page and total widget caps in one pass: later pages are
        // emptied once the total budget is exhausted. The seen-InstanceId set
        // spans the whole import — a foreign profile must not be able to give
        // two widgets the SAME safe id (their cache files would collide).
        int total = 0;
        var seenInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in profile.Pages)
        {
            page.Widgets ??= [];
            // Null WIDGET elements ("widgets":[null]) must not reach the
            // per-widget sanitizer or rehydration.
            page.Widgets = page.Widgets.Where(w => w is not null).ToList();

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
                SanitizeWidgetValues(page.Widgets[i], seenInstanceIds);
            }
            if (page.Widgets.Count > allowed)
            {
                page.Widgets = page.Widgets.Take(allowed).ToList();
            }
            total += allowed;
        }
    }

    private static void SanitizeWidgetValues(PlacedWidgetInstance placed, HashSet<string> seenInstanceIds)
    {
        // Untrusted JSON may carry "propertyValues": null — repair before use.
        placed.PropertyValues ??= [];

        // ActionCommand drives Process.Start / SendInput on the Hotkey widget
        // (identified by its property name, widget-agnostically): a foreign
        // profile must not silently arm command execution. Cleared whenever
        // present — ActionType is NOT required (it defaults to "Launch App",
        // so omitting it in the profile is a valid bypass). ActionCommand is
        // also Path-typed and listed in PathPropertyKeys, but this unconditional
        // clear must run FIRST: the path rules alone would preserve a safe
        // relative command. PropertyValues hold JsonElement after
        // deserialization — normalize before inspecting. Whitespace-only
        // values ("   ") are cleared too: they cannot arm anything, but the
        // "imports never carry a command" invariant stays airtight.
        if (placed.PropertyValues.TryGetValue("ActionCommand", out var raw) &&
            ConvertPropertyValue(raw, typeof(string)) is string command &&
            !string.IsNullOrEmpty(command))
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

        // TwitchChatStreamWidget's channel name rides the IRC JOIN command:
        // an embedded CR/LF would inject extra IRC lines on connect. Apply
        // the shared rule (Sdk's TwitchChannelRule — Core cannot reference
        // the Widgets assembly, so the rule lives in the lowest common layer;
        // the widget's NormalizeChannel is defense-in-depth at connect time).
        // Invalid names are cleared to empty — the widget's empty-channel
        // fallback then applies.
        if (placed.PropertyValues.TryGetValue("ChannelName", out var channelRaw) &&
            ConvertPropertyValue(channelRaw, typeof(string)) is string channel)
        {
            placed.PropertyValues["ChannelName"] = TwitchChannelRule.Sanitize(channel, "");
        }

        // InstanceId is placement identity, not user data — but a foreign
        // profile can dictate it, and widgets key cache FILE NAMES by it (the
        // weather widget: "weather_{InstanceId}.json" under the app dir). An
        // id with .. segments would escape that directory on the next fetch,
        // and a DUPLICATE safe id would collide two widgets' cache files.
        // Regenerate unless the id is a safe token that no other widget in
        // this import already claimed.
        if (!IsSafeInstanceId(placed.InstanceId) || !seenInstanceIds.Add(placed.InstanceId))
        {
            placed.InstanceId = Guid.NewGuid().ToString();
            seenInstanceIds.Add(placed.InstanceId);
        }

        // Untrusted string property values feed query keys, URLs, and the
        // display — a 10 MB import could carry a 10 MB Location. Cap every
        // string value at a generous bound so the import can never create a
        // multi-hundred-MB allocation spike at geocode/parse time.
        foreach (var key in placed.PropertyValues.Keys.ToArray())
        {
            if (ConvertPropertyValue(placed.PropertyValues[key], typeof(string)) is string text && text.Length > MaxPropertyStringLength)
            {
                placed.PropertyValues[key] = text[..MaxPropertyStringLength];
            }
        }
    }

    /// <summary>Cap for string widget-property values at the import boundary
    /// (query keys, URLs, and labels all derive from them).</summary>
    private const int MaxPropertyStringLength = 256;

    /// <summary>InstanceId safety rule: a short ASCII token (letters, digits,
    /// '-', '_') that can never escape a directory or resolve outside it.
    /// GUIDs and the app's own generated ids always pass; only foreign
    /// profiles can fail.</summary>
    public static bool IsSafeInstanceId(string? id)
        => id is { Length: > 0 and <= 64 }
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

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
        // Drive-relative ("C:foo") is not rooted but still resolves against a
        // drive — reject it so only genuinely relative paths survive.
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return "";
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "";
        if (path.Split(['\\', '/'], StringSplitOptions.None).Any(segment => string.Equals(segment, "..", StringComparison.Ordinal))) return "";
        return path;
    }
}
