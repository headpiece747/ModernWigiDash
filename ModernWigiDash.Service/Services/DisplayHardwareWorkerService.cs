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
/// </summary>
public sealed class DisplayHardwareWorkerService(
    ChannelWriter<TouchEventInfo> touchWriter,
    IDisplayTransport transport,
    FrameDelivery frameDelivery,
    ILogger<DisplayHardwareWorkerService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private static readonly TimeSpan TouchPollInterval = TimeSpan.FromMilliseconds(16);

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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var touchTask = RunTouchPollLoop(linkedCts.Token);

        await Task.WhenAll(touchTask);
    }

    private async Task RunTouchPollLoop(CancellationToken stoppingToken)
    {
        LogToFile("Touch poll loop started");
        var keepaliveDue = Stopwatch.GetTimestamp();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!transport.IsConnected)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(keepaliveDue).TotalSeconds >= 5)
                {
                    transport.ClearTimeout();
                    keepaliveDue = now;
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

                await Task.Delay(TouchPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LogToFile($"Touch poll loop error: {ex.Message}");
                await Task.Delay(100, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Maps the raw vendor protocol byte to the SDK touch vocabulary. This is
    /// the single normalization site: the contract and the App only ever see
    /// <see cref="TouchEventType"/>. Protocol: None=0, Down=1 (contact +
    /// movement), Up=2 (release).
    /// </summary>
    public static TouchEventType NormalizeTouchType(byte raw) => raw switch
    {
        DisplayProtocolConstants.TouchTypeDown => TouchEventType.TouchDown,
        DisplayProtocolConstants.TouchTypeUp => TouchEventType.TouchUp,
        _ => TouchEventType.TouchMove
    };

    private static void LogToFile(string msg) => FileLog.Write(msg);
}
