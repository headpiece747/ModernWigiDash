using SkiaSharp;

namespace ModernWigiDash.Sdk;

public interface IModernWidget : IAsyncDisposable
{
    string InstanceId { get; set; }
    WidgetSizeMode SizeMode { get; }
    SKSize DefaultSize { get; }
    SKSize MinimumSize { get; }

    ValueTask InitializeAsync(IWidgetContext context, CancellationToken cancellationToken = default);
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

    protected IWidgetContext Context { get; private set; } = null!;

    public virtual ValueTask InitializeAsync(IWidgetContext context, CancellationToken cancellationToken = default)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
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

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
