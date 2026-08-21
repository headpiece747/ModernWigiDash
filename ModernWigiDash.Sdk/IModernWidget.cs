
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
