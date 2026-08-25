using System.Collections.Concurrent;
using System.Reflection;

namespace ModernWigiDash.Sdk;

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
    /// <see cref="WidgetMetadataAttribute.DefaultGridSize"/> preset (the
    /// property's default value when the attribute is absent), resolved once
    /// per instance.</summary>
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
    /// sentinel (the <see cref="SvgPathParseCache{T}"/> miss-box rule: a
    /// ConcurrentDictionary never stores a null factory result) so the miss is
    /// diagnosed once instead of re-running reflection and re-logging on every
    /// call.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfoBox> PropertyCache = new();

    /// <summary>
    /// The single write path for widget properties that must survive
    /// Export→Import: resolves the property (cached, a missing name logs once
    /// and writes nothing), then commits through the context's
    /// <see cref="IModernWigiDashContext.SetWidgetProperty"/> owner — instance
    /// set, change raised, and persistence into the owning placed instance's
    /// PropertyValues in one spelling. The inspector's write-back funnel
    /// commits through the same owner. Pre-initialization (the context not
    /// handed yet, e.g. an uninit'd test widget's OnTouch): the instance
    /// still gets the value and the change still fires — there is no placed
    /// instance to persist to yet.
    /// </summary>
    protected void SetProperty(string propertyName, object? value)
    {
        PropertyInfo? property = PropertyCache.GetOrAdd((GetType(), propertyName), LookupOrLog).Property;

        if (property is null)
        {
            // Nothing was set — do NOT raise the change or persist an unknown
            // key into PropertyValues (a typo'd property must not silently
            // write garbage into the export format). The miss line itself is
            // logged once, inside LookupOrLog.
            return;
        }

        if (Context is { } context)
        {
            context.SetWidgetProperty(this, property, value);
        }
        else
        {
            // Pre-initialization leg: the instance carries the value; the
            // placed half has no owner yet.
            property.SetValue(this, value);
            OnPropertyChanged(propertyName, value);
        }
    }

    private static PropertyInfoBox LookupOrLog((Type Type, string Name) key)
    {
        PropertyInfo? property = key.Type.GetProperty(key.Name);
        if (property is null)
        {
            FileLog.Write($"SetProperty: property '{key.Name}' not found on {key.Type.FullName}");
        }
        return property is null ? PropertyInfoBox.Miss : new PropertyInfoBox(property);
    }

    private sealed class PropertyInfoBox
    {
        public static readonly PropertyInfoBox Miss = new(null);

        public PropertyInfoBox(PropertyInfo? property) => Property = property;

        public PropertyInfo? Property { get; }
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
