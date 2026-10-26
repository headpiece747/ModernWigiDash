namespace ModernWigiDash.Widgets;

/// <summary>
/// Live FPS / frame-time dashboard: current FPS, frame time, 1% low, 0.1% low,
/// GPU busy %, and CPU frame time for the focused game/app, plus a rolling
/// frame-time sparkline. Frame times come from the PresentMon Service
/// (ADR-0003), rendered through <see cref="FrameTimeCanvas"/>.
/// </summary>
[WidgetMetadata("frame_time", "FPS / Frame Time", Category = "System Monitoring")]
public class FrameTimeWidget : ModernWidgetBase
{
    /// <summary>The "Accent Color" property: primary accent color.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color" property: header, label, and value color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Show Process" property: show the tracked game/process name.</summary>
    [WidgetProperty("Show Process", WidgetPropertyType.Boolean, "Show the tracked game/process name", true)]
    public bool ShowProcess { get; set; } = true;

    /// <summary>Test seam: current view (false = dashboard, true = overlay readout).</summary>
    internal bool IsOverlayView { get; set; }

    private readonly FrameTimeCanvas _canvas = new();
    private bool _disposed;

    /// <summary>
    /// Draws the frame-time view (dashboard or overlay) from the store's
    /// fresh snapshot, or the graceful unavailable placeholder when the
    /// PresentMon capture is absent.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        FrameTimeSnapshotDto? snapshot = FrameTimeStore.TryReadFresh();
        if (snapshot is null || !snapshot.IsAvailable)
        {
            _canvas.RenderPlaceholder(canvas, bounds, "Frame capture unavailable", "Install and run the PresentMon Service", text);
            return;
        }

        if (!snapshot.CaptureHealthy)
        {
            _canvas.RenderPlaceholder(canvas, bounds, "PresentMon capture inactive", "The service is not producing present data", text);
            return;
        }

        _canvas.Render(canvas, bounds, snapshot, IsOverlayView, ShowProcess, accent, text);
    }

    /// <summary>A release toggles the dashboard/overlay readout view and requests a render.</summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local space.</param>
    /// <param name="eventType">The touch event type.</param>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp)
        {
            IsOverlayView = !IsOverlayView;
            Context?.RequestRender();
        }
    }

    /// <summary>Disposes the canvas and hoisted Skia paints.</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _canvas.Dispose();
        return base.DisposeAsync();
    }
}
