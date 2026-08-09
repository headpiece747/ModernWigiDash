using SkiaSharp;

namespace ModernWigiDash.Sdk;

public interface IModernWidget : IAsyncDisposable
{
    string InstanceId { get; set; }
    WidgetSizeMode SizeMode { get; }
    SKSize DefaultSize { get; }
    SKSize MinimumSize { get; }

    ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default);
    void Render(SKCanvas canvas, SKRect bounds);
    void OnTouch(SKPoint localPoint, TouchEventType eventType);
    void OnPropertyChanged(string propertyName, object? newValue);
}

public abstract class ModernWidgetBase : IModernWidget
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    public virtual WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public virtual SKSize DefaultSize => new SKSize(408, 300); // 2x2 grid cell default
    public virtual SKSize MinimumSize => new SKSize(100, 50);

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
        GetType().GetProperty(propertyName)?.SetValue(this, value);
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
