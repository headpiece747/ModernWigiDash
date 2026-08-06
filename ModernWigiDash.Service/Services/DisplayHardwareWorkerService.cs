using System.Diagnostics;
using System.Threading.Channels;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Service.Services;

/// <summary>
/// Worker service managing the physical USB display hardware connection and frame buffer streaming.
/// Also polls for touch input from the display and relays events via a channel.
/// </summary>
public sealed class DisplayHardwareWorkerService(
    ChannelReader<byte[]> frameChannelReader,
    ChannelWriter<DisplayTouchInput> touchWriter,
    DisplayHidTransport transport,
    ILogger<DisplayHardwareWorkerService> logger) : BackgroundService
{
    private long _framesProcessed;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");

    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TouchPollInterval = TimeSpan.FromMilliseconds(16);

    public long TotalFramesProcessed => Volatile.Read(ref _framesProcessed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogToFile("Starting Display Hardware Worker Service...");
        logger.LogDebug("Display Hardware Worker starting");

        bool connected = await transport.ConnectAsync(stoppingToken);
        LogToFile($"Display Device Connection: {connected} ({transport.DevicePath})");

        if (connected)
        {
            await transport.SetBrightnessAsync(100, stoppingToken);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var frameTask = RunFrameLoop(linkedCts.Token);
        var touchTask = RunTouchPollLoop(linkedCts.Token);

        await Task.WhenAll(frameTask, touchTask);
    }

    private async Task RunFrameLoop(CancellationToken stoppingToken)
    {
                LogToFile("[FrameLoop] Starting frame loop");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(FrameTimeout);

                try
                {
                    if (await frameChannelReader.WaitToReadAsync(timeoutCts.Token))
                    {
                        // Drain all queued frames, only send the latest to USB
                        // (avoids replaying stale frames when WCF is faster than USB)
                        byte[]? latestFrame = null;
                        while (frameChannelReader.TryRead(out var frameBuffer))
                        {
                            if (frameBuffer != null && frameBuffer.Length > 0)
                                latestFrame = frameBuffer;
                        }

                        if (latestFrame != null)
                        {
                            bool success = await transport.SendFrameAsync(latestFrame);
                            if (success)
                            {
                                long count = Interlocked.Increment(ref _framesProcessed);
                                if (count <= 5 || count % 60 == 0)
                                    LogToFile($"[HW] Frame #{count} sent to USB ({latestFrame.Length} bytes)");
                            }
                            else
                            {
                                LogToFile($"[HW] Frame send FAILED (buffer={latestFrame.Length} bytes)");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    LogToFile("[FrameLoop] Timeout - no frames received");
                    // No-op on timeout — don't clear the display or send any commands.
                    // The display should keep showing the last frame it received.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LogToFile($"Frame loop error: {ex.Message}");
                await Task.Delay(100, stoppingToken);
            }
        }
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
                    await transport.ClearTimeoutAsync();
                    keepaliveDue = now;
                }

                var touch = transport.ReadTouch();
                if (touch is DisplayHidTransport.TouchReport t)
                {
                    var evt = new DisplayTouchInput
                    {
                        Type = t.Type,
                        X = t.X,
                        Y = t.Y,
                        Timestamp = DateTime.UtcNow
                    };

                    touchWriter.TryWrite(evt);
                    LogToFile($"[TOUCH] Type={t.Type} X={t.X} Y={t.Y}");
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

    private static void LogToFile(string msg)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
        catch (IOException)
        {
            System.Diagnostics.Debug.WriteLine("LogToFile: log file locked or unavailable");
            // Log file may be locked or unavailable; silently ignore
        }
    }
}

/// <summary>
/// Touch input event from the display hardware.
/// </summary>
public sealed class DisplayTouchInput
{
    public byte Type { get; init; }
    public short X { get; init; }
    public short Y { get; init; }
    public DateTime Timestamp { get; init; }
}
