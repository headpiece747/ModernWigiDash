using System.Reflection;
using System.Text.Json;
using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.Core.Models;

/// <summary>
/// Profile operations: page CRUD, widget placement/rehydration, and JSON
/// export/import. The window keeps only dialogs, selection, and refresh.
/// Widgets are rehydrated
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

    /// <summary>
    /// Replaces the active profile with an imported one: disposes every widget
    /// instance of <paramref name="current"/> and returns
    /// <paramref name="imported"/> as the new active profile. The swap is one
    /// site, so there is no in-between state where two profiles own live
    /// widgets.
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
    /// widget cannot be both centered and cell-aligned — centering wins).
    /// The size comes from the catalog
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
    /// Export/Import round-trips). A size the input omitted (the model default
    /// still stands) is repaired to the widget's declared preset — explicit
    /// sizes win. Failures are contained per widget: one throwing widget
    /// (broken constructor or InitializeAsync) is logged and skipped so it
    /// cannot abort the whole import.
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

            // A size the imported JSON omitted (the model default still
            // stands) falls back to the widget's declared preset — the same
            // fallback PlaceWidget applies — so a hand-crafted profile missing
            // width/height rehydrates at the widget's own size, not the
            // model's 2×2. Explicit sizes win.
            if (!placed.WidthPresent) placed.Width = instance.DefaultSize.Width;
            if (!placed.HeightPresent) placed.Height = instance.DefaultSize.Height;

            // The instance and the placed widget share one identity: the
            // placed's InstanceId survives Export/Import, so rehydration must
            // sync it back onto the fresh instance (widgets key caches by it).
            instance.InstanceId = placed.InstanceId;

            ValueTask init = instance.InitializeAsync(context);
            if (init.IsCompletedSuccessfully)
            {
                // The sync module's documented invariant: every widget's
                // initialization completes synchronously, so this wait is a
                // no-op that blocks no thread (the IsCompletedSuccessfully
                // guard is what makes that true — see the else branch).
#pragma warning disable S6966 // guarded by the IsCompletedSuccessfully check above — no async work is awaited synchronously
                init.GetAwaiter().GetResult();
#pragma warning restore S6966
            }
            else
            {
                // A widget that YIELDS from InitializeAsync would deadlock the
                // UI thread here: its continuation posts back to the WPF
                // SynchronizationContext this call site is about to block on.
                // Fail it loudly instead of a silent freeze — the widget is
                // skipped (the rest of the profile still loads) and its
                // teardown is detached to a background task.
                context.LogError($"Widget '{placed.PluginId}' yielded from InitializeAsync; synchronous rehydration cannot block the UI thread — the widget is skipped.");
                _ = SkipAndDisposeAsync(init, instance, placed.PluginId, context);
                return null;
            }

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
                ValueTask dispose = instance.DisposeAsync();
                if (dispose.IsCompletedSuccessfully)
                {
#pragma warning disable S6966 // guarded by the IsCompletedSuccessfully check above — no async work is awaited synchronously
                    dispose.GetAwaiter().GetResult();
#pragma warning restore S6966
                }
                else
                {
                    FileLog.Write($"[PROFILE] Widget disposal yielded from DisposeAsync (instance {placed.InstanceId}) — teardown detached to a background task.");
                    _ = DetachDisposeAsync(dispose);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Tears down a placed widget's active instance. Widget teardown (timers,
    /// sockets, subscriptions) must never break profile operations, so failures
    /// are swallowed. The sync module's invariant is that teardown completes
    /// synchronously (the IsCompletedSuccessfully guard makes the wait a
    /// no-op); a widget that yields is detached to a background task instead
    /// of blocking the UI thread on its WPF-posted continuation.
    /// </summary>
    private static void DisposeWidgetInstance(PlacedWidgetInstance? placed)
    {
        if (placed?.ActiveInstance is null) return;
        ValueTask dispose = placed.ActiveInstance.DisposeAsync();
        if (dispose.IsCompletedSuccessfully)
        {
#pragma warning disable S6966 // guarded by the IsCompletedSuccessfully check above — no async work is awaited synchronously
            dispose.GetAwaiter().GetResult();
#pragma warning restore S6966
        }
        else
        {
            // The instance is already detached from the profile (nulled below),
            // so its in-flight teardown cannot outlive a rendered frame — it
            // just cannot be awaited on this thread.
            FileLog.Write($"[PROFILE] Widget disposal yielded from DisposeAsync (instance {placed.InstanceId}) — teardown detached to a background task.");
            _ = DetachDisposeAsync(dispose);
        }
        placed.ActiveInstance = null;
    }

    /// <summary>
    /// Completes the skipped widget's yielded initialization (on a background
    /// task — this never runs on the UI thread), then disposes the instance so
    /// a skipped widget leaves no orphaned resources (the skipped NowPlaying
    /// monitor, for instance, is torn down here).
    /// </summary>
    private static async Task SkipAndDisposeAsync(ValueTask init, IModernWidget instance, string pluginId, IModernWigiDashContext context)
    {
        try
        {
            await init.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.LogError($"Widget '{pluginId}' initialization faulted after it was skipped.", ex);
        }

        try
        {
            await instance.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Teardown of the skipped instance must not surface.
        }
    }

    /// <summary>Awaiting a yielded teardown off-thread; failures are swallowed
    /// by design (widget teardown must not break profile operations).</summary>
    private static async Task DetachDisposeAsync(ValueTask dispose)
    {
        try
        {
            await dispose.ConfigureAwait(false);
        }
        catch
        {
            // Swallowed by design.
        }
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
                ProfileImportSanitizer.SanitizeImportedProfile(loaded);
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

}
