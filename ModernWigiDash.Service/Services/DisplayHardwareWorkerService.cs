using ModernWigiDash.Sdk;
using System.Diagnostics;
using System.Threading.Channels;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Service.Services;

/// <summary>
/// Worker service managing the physical USB display hardware connection and touch input.
/// Frame delivery is owned by the shared <see cref="FrameDelivery"/> module: the WCF
/// service pushes bytes in, and this worker's only frame role is exposing delivery stats.
/// The touch+keepalive loop is a <see cref="PollLoop"/> registration — the same loop
/// shape the App uses for its WCF producers.
/// </summary>
public sealed class DisplayHardwareWorkerService(
    ChannelWriter<TouchEventInfo> touchWriter,
    IDisplayTransport transport,
    FrameDelivery frameDelivery,
    ILogger<DisplayHardwareWorkerService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private PollLoop? _touchLoop;
    private long _keepaliveDue;

    public long TotalFramesProcessed => frameDelivery.FramesSent;

    /// <summary>
    /// Puts the display into standby before the host shuts the service down
    /// (sc stop, machine shutdown, crash recovery). The transport is still
    /// connected at this point — singletons are disposed after hosted services
    /// stop. The welcome screen shows immediately; without the 5s ClearTimeout
    /// heartbeats the display then sleeps on its own timeout.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _touchLoop?.Stop();
        try
        {
            if (transport.IsConnected)
            {
                bool standby = transport.GoToStandby();
                LogToFile(standby ? "[STANDBY] Display set to standby on service stop"
                                  : "[STANDBY] Standby command failed on service stop");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[STANDBY] Standby on service stop failed: {ex.Message}");
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogToFile("Starting Display Hardware Worker Service...");
        logger.LogDebug("Display Hardware Worker starting");

        bool connected = transport.Connect();
        LogToFile($"Display Device Connection: {connected} ({transport.DevicePath})");

        if (connected)
        {
            transport.SetBrightness(100);
        }

        _keepaliveDue = Stopwatch.GetTimestamp();
        _touchLoop = new PollLoop(
            "TOUCH",
            TimeSpan.FromMilliseconds(16),
            ready: () => transport.IsConnected,
            tick: TouchTick,
            onTickFailure: () => { },
            log: msg => LogToFile(msg));
        _touchLoop.Start();
        LogToFile("Touch poll loop started");

        // Keep the hosted task alive until cancellation; the loop runs on its own task.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    /// <summary>
    /// One touch+keepalive tick: refreshes the display keepalive every 5s and
    /// relays a raw touch report, normalized once at this transport seam.
    /// </summary>
    private void TouchTick()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_keepaliveDue).TotalSeconds >= 5)
        {
            transport.ClearTimeout();
            _keepaliveDue = now;
        }

        var touch = transport.ReadTouch();
        if (touch is TouchReport t)
        {
            var evt = new TouchEventInfo
            {
                Type = NormalizeTouchType(t.Type),
                X = t.X,
                Y = t.Y,
                TimestampUtcTicks = _timeProvider.GetUtcNow().UtcTicks
            };

            touchWriter.TryWrite(evt);
        }
    }

    /// <summary>
    /// Maps the raw vendor protocol byte to the SDK touch vocabulary. Delegates
    /// to <see cref="TouchReport.ToEventType"/> — the single normalization site
    /// in the protocol layer, shared with the App's direct-USB engine. The
    /// contract and the App only ever see <see cref="TouchEventType"/>.
    /// </summary>
    public static TouchEventType NormalizeTouchType(byte raw) => TouchReport.ToEventType(raw);

    private static void LogToFile(string msg) => FileLog.Write(msg);
}
