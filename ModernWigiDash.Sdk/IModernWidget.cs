using System.Collections.Concurrent;
using System.Reflection;
using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The widget contract every display widget implements. The host discovers
/// widget types by reflection (parameterless constructor, <c>[WidgetMetadata]</c>),
/// instantiates them per placement, and drives the lifecycle:
/// <see cref="InitializeAsync"/> once at placement, <see cref="Render"/> on the
/// 30 FPS presentation tick, <see cref="OnTouch"/> for compositor-routed touch,
/// <see cref="OnPropertyChanged"/> on external property writes, and
/// <see cref="IAsyncDisposable.DisposeAsync"/> at teardown. Because widgets are
/// reflectively instantiated they cannot take constructor dependencies — the
/// <see cref="IModernWigiDashContext"/> handed to
/// <see cref="InitializeAsync"/> is the widget's only host-service seam.
/// </summary>
public interface IModernWidget : IAsyncDisposable
{
    /// <summary>Identity of this widget instance, assigned by the host when the
    /// widget is placed. The host resolves the owning placed instance by this
    /// identity (property persistence, touch routing); widgets should treat it
    /// as host-managed and never assume a value before initialization.</summary>
    string InstanceId { get; set; }

    /// <summary>The size the widget takes when placed without an explicit size
    /// (placement centering and catalog previews use it).</summary>
    SKSize DefaultSize { get; }

    /// <summary>
    /// Host call, once per placement: hands the widget its
    /// <see cref="IModernWigiDashContext"/> and starts its lifetime. The host
    /// invokes it SYNCHRONOUSLY on the UI thread during placement and BEFORE
    /// the profile's stored properties are applied to the instance — a widget
    /// must not marshal to a SynchronizationContext and await it (a deadlock
    /// would wedge startup), and it cannot assume its persisted property
    /// values exist yet. Long-lived work (poll loops, subscriptions,
    /// background fetches) should be started here with the supplied token so
    /// teardown can cancel it; the base stores the context, so overrides must
    /// call <c>base.InitializeAsync</c> first.
    /// </summary>
    ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default);

    /// <summary>Host call on every 30 FPS presentation tick: draws the widget
    /// into <paramref name="canvas"/> within <paramref name="bounds"/> (the
    /// placement rect). Rendering must stay allocation-light — per-frame
    /// allocations are a measured cost in the hot pipeline.</summary>
    void Render(SKCanvas canvas, SKRect bounds);

    /// <summary>Host call for compositor-routed touch input. The point is
    /// already in the widget's local coordinate space (the compositor
    /// transformed it through the placement transform, including rotation), so
    /// widgets hit-test against their own layout. Tap actions conventionally
    /// fire on <see cref="TouchEventType.TouchUp"/>.</summary>
    void OnTouch(SKPoint localPoint, TouchEventType eventType);

    /// <summary>Host call when a property's value changed outside the widget
    /// (inspector write-back); <see cref="ModernWidgetBase.SetProperty"/> also
    /// raises it on internal mutation. The default implementation requests a
    /// repaint; overrides react to specific properties (restart loops, clamp
    /// values) and must call the base to keep the repaint.</summary>
    void OnPropertyChanged(string propertyName, object? newValue);
}

/// <summary>
/// The default widget implementation: stores the context handed to
/// <see cref="InitializeAsync"/>, owns the cached color parsing and the single
/// property-write path (<see cref="SetProperty"/>), and gives every lifecycle
/// hook a benign default. Widgets derive from this (public, parameterless
/// constructor for the reflection loader) and override only what they do.
/// </summary>
public abstract class ModernWidgetBase : IModernWidget
{
    /// <summary>A fresh GUID identity per instance — the host may replace it
    /// with the placed instance's persisted identity.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    // The nominal default size is declared on the [WidgetMetadata] attribute
    // (the catalog reads the same fact at registration), so a widget that
    // declares its preset there needs no override for its instance to agree
    // with the catalog. A non-preset default can still override.
    private SKSize? _defaultSize;

    /// <summary>The default placement size: this type's
    /// <see cref="WidgetMetadataAttribute.DefaultGridSize"/> preset (the 2×2
    /// house size when the attribute is absent), resolved once per instance.</summary>
    public virtual SKSize DefaultSize
    {
        get
        {
            if (_defaultSize is null)
            {
                // this.GetType() (not typeof(this)): the generic call after
                // the typeof-paren trips the parser's qualified-type
                // ambiguity in expression positions.
                _defaultSize = this.GetType().GetCustomAttribute<WidgetMetadataAttribute>()?.DefaultGridSize.ToSize()
                    ?? GridSizePreset.Size2x2.ToSize();
            }
            return _defaultSize.Value;
        }
    }

    /// <summary>The host-services seam, set by the base
    /// <see cref="InitializeAsync"/>. Null before initialization — guard
    /// (<c>Context?.X</c>) when teardown work may run after the context is
    /// gone.</summary>
    protected IModernWigiDashContext Context { get; private set; } = null!;

    /// <summary>Stores the host context. Overrides must call this first
    /// (<c>await base.InitializeAsync(context, cancellationToken)</c>) before
    /// using <see cref="Context"/>.</summary>
    public virtual ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        return ValueTask.CompletedTask;
    }

    /// <summary>Draws the widget into the placement rect (see
    /// <see cref="IModernWidget.Render"/>).</summary>
    public abstract void Render(SKCanvas canvas, SKRect bounds);

    /// <summary>Default: no-op. Override for interactive touch buttons; see
    /// <see cref="IModernWidget.OnTouch"/> for the coordinate contract.</summary>
    public virtual void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        // Default: no-op, override for interactive touch buttons
    }

    /// <summary>Default: request a repaint. Override to react to specific
    /// property changes, then call the base.</summary>
    public virtual void OnPropertyChanged(string propertyName, object? newValue)
    {
        // Default: request render when property changes
        Context?.RequestRender();
    }

    private readonly Dictionary<string, SKColor> _colorCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Parses a hex color string, caching the result per distinct value, so
    /// the per-frame <c>SKColor.TryParse</c> sites across the widget layer run
    /// at most once per value. The cache is bounded by the number of distinct
    /// rendered color values a widget can have (a handful), not by call sites;
    /// fallback semantics are declared once instead of at 27 sites.
    /// </summary>
    protected SKColor ColorOf(string hex, SKColor fallback)
    {
        if (_colorCache.TryGetValue(hex, out SKColor cached))
        {
            return cached;
        }

        SKColor color = SKColor.TryParse(hex, out SKColor parsed) ? parsed : fallback;
        _colorCache[hex] = color;
        return color;
    }

    /// <summary>
    /// Cached reflection lookup per (type, property name): SetProperty runs on
    /// inspector write-back and touch toggles; a GetProperty reflection call
    /// per write is measurable on the 30 FPS path. A missing property caches a
    /// sentinel so the miss is diagnosed once instead of thrashing reflection.
    /// </summary>
    // Cached PropertyInfo per (type, name) — a miss is cached as null so a
    // repeated typo doesn't re-reflect every call.
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> PropertyCache = new();

    /// <summary>
    /// The single write path for widget properties that must survive
    /// Export→Import: sets the instance property, raises
    /// <see cref="OnPropertyChanged"/>, and persists the value into the owning
    /// placed instance's PropertyValues via the context. Every mutation path
    /// (inspector write-back, icon-grab moves, widget OnTouch toggles) routes
    /// through this or the inspector's equivalent, so the instance ↔
    /// PropertyValues invariant has exactly one owner instead of being spread
    /// across modules with the occasional violation.
    /// </summary>
    protected void SetProperty(string propertyName, object? value)
    {
        PropertyInfo? property = PropertyCache.GetOrAdd(
            (GetType(), propertyName),
            static key => key.Type.GetProperty(key.Name));

        if (property is null)
        {
            string message = $"SetProperty: property '{propertyName}' not found on {GetType().FullName}";
            FileLog.Write(message);
            // Nothing was set — do NOT raise the change or persist an unknown
            // key into PropertyValues (a typo'd property must not silently
            // write garbage into the export format).
            return;
        }

        property.SetValue(this, value);

        OnPropertyChanged(propertyName, value);
        Context?.PersistProperty(this, propertyName, value);
    }

    /// <summary>Host call at teardown (widget removed, profile closed):
    /// release owned resources — poll loops, feed subscriptions, tokens. The
    /// default does nothing. Widgets that cancel long-lived work should use
    /// the token they captured in <see cref="InitializeAsync"/>.</summary>
    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Optional interface widgets can implement to expose inspector buttons
/// (WidgetPropertyType.Button) that trigger a widget-specific action.
/// </summary>
public interface IWidgetActionInvoker
{
    /// <summary>Host call when the inspector button for
    /// <paramref name="propertyName"/> is clicked; the widget runs its action
    /// (e.g. Twitch login) and refreshes the inspector/render via the context.</summary>
    void InvokeWidgetAction(string propertyName);
}
