using System.Collections.Concurrent;
using System.Reflection;
using SkiaSharp;

namespace ModernWigiDash.Sdk;

public interface IModernWidget : IAsyncDisposable
{
    string InstanceId { get; set; }
    SKSize DefaultSize { get; }

    ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default);
    void Render(SKCanvas canvas, SKRect bounds);
    void OnTouch(SKPoint localPoint, TouchEventType eventType);
    void OnPropertyChanged(string propertyName, object? newValue);
}

public abstract class ModernWidgetBase : IModernWidget
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    // 2x2 grid cell default (406x296), matching GridSizePreset.Size2x2.
    public virtual SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    protected IModernWigiDashContext Context { get; private set; } = null!;

    public virtual ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        return ValueTask.CompletedTask;
    }

    public abstract void Render(SKCanvas canvas, SKRect bounds);

    public virtual void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        // Default: no-op, override for interactive touch buttons
    }

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
            System.Diagnostics.Debug.WriteLine(message);
            FileLog.Write(message);
        }
        else
        {
            property.SetValue(this, value);
        }

        OnPropertyChanged(propertyName, value);
        Context?.PersistProperty(this, propertyName, value);
    }

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
    void InvokeWidgetAction(string propertyName);
}
