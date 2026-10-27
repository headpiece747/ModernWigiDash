using System;
using SkiaSharp;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Deep presentation pipeline module: unifies 30 FPS cadence scheduling,
/// zero-allocation Skia canvas composition, edit-mode overlay rendering,
/// and hardware frame delivery into a single cohesive interface.
/// <para>
/// Eliminates loose closure orchestration in <see cref="MainWindow"/>,
/// encapsulates the ADR-0012 non-overlapping compose-and-send invariant,
/// and provides a testable interface for headless runners and presentation tests.
/// </para>
/// </summary>
internal sealed class FramePipeline : IDisposable
{
    private readonly SkiaFrameCompositor _compositor;
    private readonly FrameDelivery _delivery;
    private readonly FramePump _pump;
    private readonly Func<PageLayout?> _activePageProvider;
    private readonly Action? _requestRepaint;
    private int _disposed;

    /// <param name="activePageProvider">Supplies the currently active page layout for composition.</param>
    /// <param name="sendFrame">Hardware or test frame transmission seam.</param>
    /// <param name="canSendFrames">Readiness predicate for frame delivery.</param>
    /// <param name="requestRepaint">Asks the host window/canvas to invalidate its visual.</param>
    /// <param name="onTick">Optional callback on every tick (e.g. badge/telemetry updates).</param>
    /// <param name="encoder">Optional RGB565 encoder; defaults to <see cref="SkiaRgb565Encoder"/>.</param>
    /// <param name="log">Optional logging sink for hardware transfer diagnostics.</param>
    /// <param name="interval">Optional cadence interval; defaults to 30 FPS.</param>
    public FramePipeline(
        Func<PageLayout?> activePageProvider,
        Func<ReadOnlyMemory<byte>, FrameSendResult> sendFrame,
        Func<bool> canSendFrames,
        Action? requestRepaint = null,
        Action? onTick = null,
        IRgb565Encoder? encoder = null,
        Action<string>? log = null,
        TimeSpan? interval = null)
        : this(
            activePageProvider,
            FrameDelivery.Create(
                encoder: encoder ?? new SkiaRgb565Encoder(),
                send: sendFrame,
                isReady: canSendFrames,
                log: log),
            compositor: null,
            requestRepaint: requestRepaint,
            onTick: onTick,
            interval: interval)
    {
    }

    /// <summary>
    /// Constructor accepting an existing or injected <see cref="FrameDelivery"/> and optional <see cref="SkiaFrameCompositor"/>.
    /// </summary>
    public FramePipeline(
        Func<PageLayout?> activePageProvider,
        FrameDelivery delivery,
        SkiaFrameCompositor? compositor = null,
        Action? requestRepaint = null,
        Action? onTick = null,
        TimeSpan? interval = null)
    {
        _activePageProvider = activePageProvider;
        _delivery = delivery;
        _compositor = compositor ?? new SkiaFrameCompositor();
        _requestRepaint = requestRepaint;

        _pump = new FramePump(
            composeAndSend: ComposeAndSend,
            requestRepaint: () => _requestRepaint?.Invoke(),
            onTick: onTick,
            composeGate: () => !_delivery.IsSendInFlight,
            interval: interval);
    }

    /// <summary>The composited frame buffer (the exact SKBitmap sent to the display and painted to the desktop preview).</summary>
    public SKBitmap CurrentFrame => _compositor.FrameBuffer;

    /// <summary>Whether the edit-mode overlay (grid, selection chrome) is drawn.</summary>
    public bool IsEditMode
    {
        get => _compositor.IsEditMode;
        set => _compositor.IsEditMode = value;
    }

    /// <summary>The placement selected in edit mode (drives selection outline), or null.</summary>
    public PlacedWidgetInstance? SelectedWidget
    {
        get => _compositor.SelectedWidget;
        set => _compositor.SelectedWidget = value;
    }

    /// <summary>Underlying compositor module.</summary>
    internal SkiaFrameCompositor Compositor => _compositor;

    /// <summary>Underlying frame delivery module.</summary>
    internal FrameDelivery Delivery => _delivery;

    /// <summary>Underlying pump module.</summary>
    internal FramePump Pump => _pump;

    /// <summary>Starts the presentation loop.</summary>
    public void Start() => _pump.Start();

    /// <summary>Stops the presentation loop (e.g. on power suspend or window minimization).</summary>
    public void Stop() => _pump.Stop();

    /// <summary>Manually executes a single tick (used in tests and simulation).</summary>
    public void Tick() => _pump.Tick();

    /// <summary>Total frames successfully sent by the delivery sink.</summary>
    public long FramesSent => _delivery.FramesSent;

    /// <summary>Total frames dropped by the delivery coalescer.</summary>
    public long DroppedCount => _delivery.DroppedCount;

    /// <summary>Whether a frame transmission is currently in flight over the hardware transport.</summary>
    public bool IsSendInFlight => _delivery.IsSendInFlight;

    private void ComposeAndSend()
    {
        if (_activePageProvider() is { } page)
        {
            _compositor.Compose(page);
            _delivery.Push(_compositor.FrameBuffer);
        }
    }

    /// <summary>Teardown step: stops the cadence timer.</summary>
    internal void DisposePump() => _pump.Dispose();

    /// <summary>Teardown step: flushes and stops frame delivery.</summary>
    internal void DisposeDelivery() => _delivery.Dispose();

    /// <summary>Teardown step: disposes the Skia compositor resources.</summary>
    internal void DisposeCompositor() => _compositor.Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DisposePump();
        DisposeDelivery();
        DisposeCompositor();
    }
}
