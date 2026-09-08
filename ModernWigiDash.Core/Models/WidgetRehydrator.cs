using System.Reflection;
using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.Core.Models;

/// <summary>
/// Widget rehydration: creates and initializes the active widget instance for
/// a placed widget, then applies the user-configured custom property values
/// (surviving Export/Import round-trips). A size the input omitted (the model
/// default still stands) is repaired to the widget's declared preset — explicit
/// sizes win. Failures are contained per widget: one throwing widget (broken
/// constructor or InitializeAsync) is logged and skipped so it cannot abort the
/// whole import. The sync module's invariant is that initialization and teardown
/// complete synchronously (the IsCompletedSuccessfully guards make those waits
/// no-ops); a widget that yields is skipped with its teardown detached to a
/// background task instead of blocking the UI thread on its WPF-posted
/// continuation. ProfileOps routes its placement and import paths through this
/// module, so the create/init/property/dispose sequencing has one home and a
/// direct test surface.
/// </summary>
public static class WidgetRehydrator
{
    // One bound spelling of the PROFILE tag (cadence 1: the rare disposal-yield
    // event always logs); the line policy (flatten + bound + redact) is owned
    // by FileLog.Write, so the sites emit message only.
    private static readonly DiagLog _log = new("PROFILE", 1);

    /// <summary>
    /// Creates and initializes the active widget instance for a placed widget,
    /// then applies the user-configured custom property values (surviving
    /// Export/Import round-trips). Returns null when the plugin is unknown,
    /// broken, or yielded from InitializeAsync — each skip is logged, never
    /// silent.
    /// </summary>
    public static IModernWidget? Rehydrate(
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        PlacedWidgetInstance placed)
    {
        IModernWidget? instance = null;
        try
        {
            WidgetCreateResult created = loader.CreateInstanceResult(placed.PluginId);
            if (created is WidgetCreateResult.Ok(var widget))
            {
                instance = widget;
            }
            else if (created is WidgetCreateResult.Broken(var reason))
            {
                context.LogError($"Widget '{placed.PluginId}' is broken and was skipped: {reason}");
                return null;
            }
            // WidgetCreateResult.NotFound: a hand-crafted or older profile can
            // name a plugin this build does not register; the skip is logged,
            // never silent.
            else
            {
                context.LogError($"Widget '{placed.PluginId}' is not in the catalog and was skipped");
                return null;
            }

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

            DisposeInstance(placed);
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
                    _log.Write(() => $"Widget disposal yielded from DisposeAsync (instance {placed.InstanceId}); teardown detached to a background task.");
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
    public static void DisposeInstance(PlacedWidgetInstance? placed)
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
            _log.Write(() => $"Widget disposal yielded from DisposeAsync (instance {placed.InstanceId}); teardown detached to a background task.");
            _ = DetachDisposeAsync(dispose);
        }
        placed.ActiveInstance = null;
    }

    /// <summary>
    /// Imported JSON dictionaries arrive as JsonElement values; deserialize
    /// them into the real type. Shared with the sanitizer's cross-call use.
    /// </summary>
    public static object? ConvertPropertyValue(object? raw, Type targetType)
        => ProfileOps.ConvertPropertyValue(raw, targetType);

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
}
