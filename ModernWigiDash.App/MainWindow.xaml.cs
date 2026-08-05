using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Hardware;
using ModernWigiDash.Hardware.Transport;

using ModernWigiDash.Sdk;
using ModernWigiDash.Service.Wcf;
using ModernWigiDash.Widgets;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ModernWigiDash.App;

public partial class MainWindow : Window, ModernWigiDashContext, IWidgetHostInteraction
{
    private readonly WidgetPluginLoader _loader = new();
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly DisplayDeviceEngine _usbDevice = new();
    private readonly DispatcherTimer _renderTimer = new();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    /// <summary>
    /// WCF client for routing frames to the Windows Service.
    /// When the ModernWigiDash.Service is running (as LocalSystem), it owns the USB device.
    /// The App routes frames through WCF instead of connecting directly.
    /// This matches the vendor WigiDashService architecture.
    /// </summary>
    private ModernWigiDashDisplayServiceClient? _wcfClient;

    /// <summary>
    /// Whether the Windows Service is active and handling USB communication.
    /// </summary>
    private volatile bool _serviceActive = false;

    private ProfileLayout _profile = new();
    private PlacedWidgetInstance? _selectedWidget;
    private Window? _deviceAuthorizationWindow;

    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "display_device.log");

    // Mouse & Swipe Gesture Interaction State
    private bool _isMouseDown = false;
    private bool _isDraggingWidget = false;
    private bool _isResizingWidget = false;
    private bool _isDraggingIcon = false;
    private bool _iconDragMoved = false;
    private Point _iconGrabOffset;
    private const float ResizeHandleSize = 14f;
    private Point _lastMousePos;
    private float _swipeStartX;
    private float _swipeStartY;
    private float _hwTouchStartX;
    private float _hwTouchStartY;
    private bool _hwTouchActive = false;
    private bool _isUpdatingInspector = false;

    // Async frame pipeline — decouple UI render timer from WCF round-trip
    private readonly Channel<byte[]> _frameChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    {
        SingleWriter = true,
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private CancellationTokenSource? _frameSenderCts;
    private byte[]? _rgb565PoolBuffer;

    // Touch polling via WCF (polls hardware touch at 50ms intervals, off UI thread)
    private CancellationTokenSource? _touchPollCts;

    // Sensor polling via WCF (refreshes the LHM snapshot cache off UI thread)
    private CancellationTokenSource? _sensorPollCts;

    // Frame-time polling via WCF (refreshes the ETW FPS/frame-time snapshot cache off UI thread)
    private CancellationTokenSource? _frameTimePollCts;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTheme();
        PreviewMouseDown += OnWindowPreviewMouseDown;

        // Detect if Windows Service is running and initialize WCF client for frame routing.
        // Fire-and-forget async to avoid blocking the UI thread during port detection.
        _ = InitializeWcfRoutingAsync();

        // Connection is already fired async by DisplayDeviceEngine constructor.
        // Do NOT block the UI thread waiting for USB — the render timer will
        // start sending frames as soon as the background connection succeeds.

        // 1. Register Built-In Display Suite
        _loader.RegisterBuiltInPlugin(typeof(DigitalAnalogClockWidget));
        _loader.RegisterBuiltInPlugin(typeof(HardwareMonitorWidget));
        _loader.RegisterBuiltInPlugin(typeof(FrameTimeWidget));
        _loader.RegisterBuiltInPlugin(typeof(AudioVisualizerWidget));
        _loader.RegisterBuiltInPlugin(typeof(NowPlayingWidget));
        _loader.RegisterBuiltInPlugin(typeof(HotkeyButtonWidget));
        _loader.RegisterBuiltInPlugin(typeof(StopwatchTimerWidget));
        _loader.RegisterBuiltInPlugin(typeof(CryptoStockTickerWidget));
        _loader.RegisterBuiltInPlugin(typeof(PictureAndGifWidget));
        _loader.RegisterBuiltInPlugin(typeof(TwitchChatStreamWidget));
        _loader.RegisterBuiltInPlugin(typeof(WeatherForecastWidget));
        _loader.RegisterBuiltInPlugin(typeof(TextLabelWidget));

        // 2. Populate Catalog UI (sorted alphabetically by display name)
        ListCatalog.ItemsSource = _loader.RegisteredPlugins.OrderBy(p => p.DisplayName).ToList();

        // 3. Setup Default Profile Layout with 3 cool starter widgets
        SetupDefaultStarterLayout();
        RebuildPageTabsUI();

        // 4. Hook up USB Hardware physical touch events to Skia RouteTouch & hardware page swipe navigation
        _usbDevice.OnTouchEvent += (point, touchType) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (touchType == TouchEventType.TouchDown)
                {
                    // Only record start position on the first Down (hardware sends
                    // type=1 for both initial contact AND intermediate movement points)
                    if (!_hwTouchActive)
                    {
                        _hwTouchStartX = point.X;
                        _hwTouchStartY = point.Y;
                        _hwTouchActive = true;
                    }
                }
                else if (touchType == TouchEventType.TouchUp && _hwTouchActive)
                {
                    _hwTouchActive = false;
                    float deltaX = point.X - _hwTouchStartX;
                    float deltaY = point.Y - _hwTouchStartY;

                    if (_profile.Pages.Count > 1)
                    {
                        // Swipe detection
                        if (Math.Abs(deltaX) > 70 && Math.Abs(deltaY) < 80)
                        {
                            if (deltaX < -70 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                            {
                                SwitchToPage(_profile.ActivePageIndex + 1);
                                return;
                            }
                            else if (deltaX > 70 && _profile.ActivePageIndex > 0)
                            {
                                SwitchToPage(_profile.ActivePageIndex - 1);
                                return;
                            }
                        }

                        // Arrow tap fallback (stationary tap near edges)
                        if (Math.Abs(deltaX) < 30 && Math.Abs(deltaY) < 30)
                        {
                            if (point.X <= 60 && point.Y >= 200 && point.Y <= 400 && _profile.ActivePageIndex > 0)
                            {
                                SwitchToPage(_profile.ActivePageIndex - 1);
                                return;
                            }
                            if (point.X >= 964 && point.Y >= 200 && point.Y <= 400 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                            {
                                SwitchToPage(_profile.ActivePageIndex + 1);
                                return;
                            }
                        }
                    }
                }

                // Map hardware touch type to widget touch event
                // Hardware only sends: Down(1) for contact+movement, Up(2) for release
                TouchEventType widgetTouchType;
                if (touchType == TouchEventType.TouchDown && _hwTouchActive &&
                    (Math.Abs(_hwTouchStartX - point.X) > 0.5f || Math.Abs(_hwTouchStartY - point.Y) > 0.5f))
                {
                    widgetTouchType = TouchEventType.TouchMove;
                }
                else if (touchType == TouchEventType.TouchDown)
                {
                    widgetTouchType = TouchEventType.TouchDown;
                }
                else if (touchType == TouchEventType.TouchUp)
                {
                    widgetTouchType = TouchEventType.TouchUp;
                }
                else
                {
                    widgetTouchType = touchType;
                }
                SkiaFrameCompositor.RouteTouch(_profile.ActivePage, point.X, point.Y, widgetTouchType);
                SkiaCanvas.InvalidateVisual();
            });
        };

        // 5. Start 30 FPS Skia Render Loop & Hardware Frame Streamer
        _renderTimer.Interval = TimeSpan.FromMilliseconds(33.3); // 30 FPS
        _renderTimer.Tick += (s, e) =>
        {
            _compositor.Compose(_profile.ActivePage, 30.0f, _profile.ActivePageIndex, _profile.Pages.Count);
            SkiaCanvas.InvalidateVisual();

            // Push frame to async channel (non-blocking, drop oldest if backlogged)
            if (_serviceActive && _wcfClient != null)
            {
                ConvertToRgb565(_compositor.FrameBuffer, ref _rgb565PoolBuffer);
                // Copy to channel — channel may hold the buffer briefly
                byte[] frameCopy = new byte[_rgb565PoolBuffer!.Length];
                Buffer.BlockCopy(_rgb565PoolBuffer, 0, frameCopy, 0, _rgb565PoolBuffer.Length);
                _frameChannel.Writer.TryWrite(frameCopy);
            }
            else if (_usbDevice.IsConnected && !_usbDevice.IsSimulationMode)
            {
                _usbDevice.SendFrameBuffer(_compositor.FrameBuffer);
            }

            UpdateUsbBadge();
        };
        _renderTimer.Start(); // Start the render loop immediately

        // Start background frame sender (decouples WCF round-trip from render loop)
        _frameSenderCts = new CancellationTokenSource();
        _ = Task.Run(() => FrameSenderLoop(_frameSenderCts.Token));

        // 6. Clean lifecycle shutdown on window close / debugging stop
        Closed += (s, e) =>
        {
            _renderTimer.Stop();
            _touchPollCts?.Cancel();
            _touchPollCts?.Dispose();
            _sensorPollCts?.Cancel();
            _sensorPollCts?.Dispose();
            _frameTimePollCts?.Cancel();
            _frameTimePollCts?.Dispose();
            _frameSenderCts?.Cancel();
            _frameSenderCts?.Dispose();

            // Reset display to standby (clear framebuffer + switch to welcome screen)
            try
            {
                _wcfClient?.Shutdown();
            }
            catch (Exception ex)
            {
                Log($"[WCF] Shutdown failed during cleanup: {ex.Message}");
            }

            _wcfClient?.Dispose();
            _deviceAuthorizationWindow?.Close();
            _compositor.Dispose();
            _usbDevice.Dispose();
        };

        // Update USB badge
        UpdateUsbBadge();
        UpdateActiveCount();
        UpdateInspectorPanel();
    }

    /// <summary>
    /// Detects if the ModernWigiDash.Windows Service is running and initializes WCF client.
    /// When the service is active, it owns the USB device handle (running as LocalSystem).
    /// The App routes frames through WCF instead of connecting directly.
    /// </summary>
    private async Task InitializeWcfRoutingAsync()
    {
        try
        {
            Log("[WCF] Detecting service port...");
            int? port = await ModernWigiDashDisplayServiceClient.DetectServicePortAsync();
            if (port.HasValue)
            {
                string endpointUrl = $"http://localhost:{port.Value}/ModernWigiDashDisplayService";
                Log($"[WCF] Port {port.Value} detected, creating client...");
                _wcfClient = new ModernWigiDashDisplayServiceClient(endpointUrl);

                try
                {
                    string version = _wcfClient.GetVersion();
                    _serviceActive = true;
                    Log($"[WCF] Connected! Version: {version}, Port: {port.Value}");

                    bool displayInit = _wcfClient.InitializeDisplay();
                    Log($"[WCF] Display initialization: {displayInit}");

                    StartTouchPolling();
                    StartSensorPolling();
                    StartFrameTimePolling();
                }
                catch (Exception ex)
                {
                    Log($"[WCF] Connected but GetVersion failed: {ex.Message}");
                    _wcfClient?.Dispose();
                    _wcfClient = null;
                    _serviceActive = false;
                }
            }
            else
            {
                Log("[WCF] No service detected. Using direct USB mode.");
            }
        }
        catch (Exception ex)
        {
            Log($"[WCF] Detection failed ({ex.Message}). Using direct USB mode.");
        }
    }

    /// <summary>
    /// Background loop that drains the frame channel and sends via WCF.
    /// When multiple frames queue up (because WCF round-trip is slower than
    /// the render timer), it drains all available frames and only sends the
    /// latest one. This keeps the display showing real-time content instead
    /// of replaying stale buffered frames.
    /// </summary>
    private async Task FrameSenderLoop(CancellationToken ct)
    {
        var reader = _frameChannel.Reader;
        int sentCount = 0;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                // Drain all queued frames, keep only the latest
                byte[]? latestFrame = null;
                while (reader.TryRead(out var frame))
                {
                    latestFrame = frame;
                }

                if (latestFrame == null) continue;

                try
                {
                    bool ok = _wcfClient?.SendFrame(latestFrame) == true;
                    sentCount++;
                    if (sentCount <= 5 || sentCount % 120 == 0)
                        Log($"[WCF] Frame #{sentCount} sent ({latestFrame.Length} bytes) ok={ok}");
                }
                catch (Exception ex)
                {
                    Log($"[WCF] Frame send failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: frame sender loop cancelled during shutdown
        }
    }

    /// <summary>
    /// Starts polling for hardware touch input via WCF at 50ms intervals.
    /// Runs on a background thread to avoid blocking the WPF UI.
    /// Routes touch events to SkiaFrameCompositor and handles page swipe navigation.
    /// </summary>
    private void StartTouchPolling()
    {
        if (_touchPollCts != null) return;

        _touchPollCts = new CancellationTokenSource();
        var ct = _touchPollCts.Token;

        _ = Task.Run(async () =>
        {
            Log("[TOUCH] Touch polling started (50ms interval via WCF, background thread)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_wcfClient == null || !_serviceActive)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }

                    var touch = _wcfClient.PollTouch();
                    if (touch != null)
                    {
                        await Dispatcher.BeginInvoke(() =>
                        {
                            ProcessHardwareTouch(touch);
                        });
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log($"[WCF] Touch poll failed: {ex.Message}");
                }

                try { await Task.Delay(16, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>
    /// Background loop that polls the service for the latest hardware sensor
    /// snapshot (~1s) and caches it in <see cref="LhmSensorStore"/> so widgets
    /// read it on the render thread without a WCF round-trip.
    /// </summary>
    private void StartSensorPolling()
    {
        if (_sensorPollCts != null) return;

        _sensorPollCts = new CancellationTokenSource();
        var ct = _sensorPollCts.Token;

        _ = Task.Run(async () =>
        {
            Log("[SENSOR] Sensor polling started (1s interval via WCF, background thread)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_wcfClient == null || !_serviceActive)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var dto = _wcfClient.GetSensorSnapshot();
                    var readings = dto?.Readings
                        .Select(r => new LhmReading(
                            r.SensorId,
                            r.SensorName,
                            $"{r.HardwareName}: {r.SensorName}",
                            r.Unit,
                            r.Value,
                            r.Min,
                            r.Max,
                            r.Avg))
                        .ToList() ?? [];

                    LhmSensorStore.Update(new LhmSnapshot(
                        dto?.IsConnected ?? false,
                        dto?.LastUpdate ?? DateTime.UtcNow,
                        readings));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log($"[WCF] Sensor poll failed: {ex.Message}");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>
    /// Resolves the process id of the currently focused (foreground) window.
    /// Returns 0 when there is no foreground window.
    /// </summary>
    private static int GetForegroundProcessId()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return 0;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            return unchecked((int)pid);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Background loop that polls the service for the latest FPS / frame-time
    /// snapshot (~1s) and caches it in <see cref="FrameTimeStore"/> so the
    /// frame-time widget reads it on the render thread without a WCF round-trip.
    /// </summary>
    private void StartFrameTimePolling()
    {
        if (_frameTimePollCts != null) return;

        _frameTimePollCts = new CancellationTokenSource();
        var ct = _frameTimePollCts.Token;

        _ = Task.Run(async () =>
        {
            Log("[FRAME] Frame-time polling started (1s interval via WCF, background thread)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_wcfClient == null || !_serviceActive)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    // Track the foreground window's process so the widget shows
                    // the focused game's FPS. When the App itself (or nothing)
                    // is focused, pass -1 so the service returns the idle view
                    // instead of falling back to the most active presenter.
                    int preferredPid = GetForegroundProcessId();
                    if (preferredPid <= 0 || preferredPid == Environment.ProcessId)
                    {
                        preferredPid = -1;
                    }

                    var dto = _wcfClient.GetFrameTimeSnapshot(preferredPid);
                    var record = new FrameTimeSnapshotRecord(
                        dto?.IsAvailable ?? false,
                        dto?.ProcessId ?? 0,
                        dto?.ProcessName ?? string.Empty,
                        dto?.Fps ?? 0,
                        dto?.FrameTimeMs ?? 0,
                        dto?.Low1PercentFps ?? 0,
                        dto?.Low01PercentFps ?? 0,
                        dto?.GpuBusyPercent ?? 0,
                        dto?.CpuFrameTimeMs ?? 0,
                        dto?.RecentFrameTimesMs ?? []);

                    FrameTimeStore.Update(record);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log($"[WCF] Frame-time poll failed: {ex.Message}");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }
    /// Handles page swipe navigation and routes to widget compositor.
    /// Hardware touch protocol: None=0, Down=1 (contact+movement), Up=2 (release).
    /// Intermediate movement points during a swipe are also sent as Down(1).
    /// </summary>
    private void ProcessHardwareTouch(TouchEventInfo touch)
    {
        if (touch.Type == DisplayProtocolConstants.TouchTypeDown)
        {
            // Only record start position on the first Down (not intermediate movement points)
            if (!_hwTouchActive)
            {
                _hwTouchStartX = touch.X;
                _hwTouchStartY = touch.Y;
                _hwTouchActive = true;
            }
        }
        else if (touch.Type == DisplayProtocolConstants.TouchTypeUp && _hwTouchActive)
        {
            _hwTouchActive = false;
            float deltaX = touch.X - _hwTouchStartX;
            float deltaY = touch.Y - _hwTouchStartY;

            if (_profile.Pages.Count > 1)
            {
                // Swipe detection
                if (Math.Abs(deltaX) > 70 && Math.Abs(deltaY) < 80)
                {
                    if (deltaX < -70 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                    {
                        SwitchToPage(_profile.ActivePageIndex + 1);
                        return;
                    }
                    else if (deltaX > 70 && _profile.ActivePageIndex > 0)
                    {
                        SwitchToPage(_profile.ActivePageIndex - 1);
                        return;
                    }
                }

                // Arrow tap fallback (stationary tap near edges)
                if (Math.Abs(deltaX) < 30 && Math.Abs(deltaY) < 30)
                {
                    if (touch.X <= 60 && touch.Y >= 200 && touch.Y <= 400 && _profile.ActivePageIndex > 0)
                    {
                        SwitchToPage(_profile.ActivePageIndex - 1);
                        return;
                    }
                    if (touch.X >= 964 && touch.Y >= 200 && touch.Y <= 400 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                    {
                        SwitchToPage(_profile.ActivePageIndex + 1);
                        return;
                    }
                }
            }
        }
        else if (touch.Type == DisplayProtocolConstants.TouchTypeUp)
        {
            // The device reports the release state for more than one poll.
            // Ignore subsequent releases so one physical tap becomes one action.
            return;
        }

        // Map hardware touch type to widget touch event
        // Hardware only sends: Down(1) for contact+movement, Up(2) for release
        TouchEventType touchEventType = touch.Type switch
        {
            DisplayProtocolConstants.TouchTypeDown => TouchEventType.TouchDown,
            DisplayProtocolConstants.TouchTypeUp => TouchEventType.TouchUp,
            _ => TouchEventType.TouchMove
        };

        SkiaFrameCompositor.RouteTouch(_profile.ActivePage, touch.X, touch.Y, touchEventType);
        SkiaCanvas.InvalidateVisual();
    }

    /// <summary>
    /// Converts an SKBitmap (RGBA8888) to RGB565 Little Endian byte array.
    /// Reuses a pooled buffer to reduce GC pressure at 60 FPS.
    /// </summary>
    private static void ConvertToRgb565(SKBitmap bitmap, ref byte[]? poolBuffer)
    {
        int width = DisplayProtocolConstants.FramebufferWidth;
        int height = DisplayProtocolConstants.FramebufferHeight;
        int frameSize = DisplayProtocolConstants.FrameBufferSize;

        if (poolBuffer == null || poolBuffer.Length < frameSize)
            poolBuffer = new byte[frameSize];

        byte[] rgb565 = poolBuffer;
        int idx = 0;

        int srcWidth = bitmap.Width;
        int srcHeight = bitmap.Height;

        if (srcWidth == width && srcHeight == height)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap != null && pixmap.GetPixels() != IntPtr.Zero)
            {
                unsafe
                {
                    byte* srcPtr = (byte*)pixmap.GetPixels();
                    fixed (byte* dstPtr = rgb565)
                    {
                        ushort* dstUshort = (ushort*)dstPtr;
                        int pixelCount = width * height;
                        for (int i = 0; i < pixelCount; i++)
                        {
                            byte b = srcPtr[i * 4];
                            byte g = srcPtr[i * 4 + 1];
                            byte r = srcPtr[i * 4 + 2];
                            dstUshort[i] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                        }
                    }
                }
                return;
            }
        }

        for (int y = 0; y < height; y++)
        {
            int srcY = srcHeight > 0 ? Math.Clamp((y * srcHeight) / height, 0, srcHeight - 1) : 0;
            for (int x = 0; x < width; x++)
            {
                int srcX = srcWidth > 0 ? Math.Clamp((x * srcWidth) / width, 0, srcWidth - 1) : 0;

                SKColor color = bitmap.GetPixel(srcX, srcY);
                ushort rgb565Pixel = (ushort)(((color.Red >> 3) << 11) | ((color.Green >> 2) << 5) | (color.Blue >> 3));

                rgb565[idx++] = (byte)(rgb565Pixel & 0xFF);
                rgb565[idx++] = (byte)(rgb565Pixel >> 8);
            }
        }
    }

    private void SetupDefaultStarterLayout()
    {
        var page = _profile.ActivePage;
        page.Widgets.Clear();

        // ── Page 1: Main Dashboard ──
        PlaceWidgetOnCanvas("clock_modern", 0, 0, 406, 148);
        PlaceWidgetOnCanvas("weather_forecast", 0, 148, 406, 148);
        PlaceWidgetOnCanvas("audio_visualizer", 0, 296, 1016, 296);
        PlaceWidgetOnCanvas("frame_time", 406, 0, 406, 148);
        PlaceWidgetOnCanvas("ticker_stock", 406, 148, 203, 148);
        PlaceWidgetOnCanvas("text_label", 610, 148, 203, 148);
        PlaceWidgetOnCanvas("hotkey_button", 813, 0, 203, 148);
        PlaceWidgetOnCanvas("stopwatch_timer", 813, 148, 203, 148);

        // ── Page 2: Now Playing ──
        int pageIndex = _profile.Pages.Count;
        var nowPlayingPage = new PageLayout { PageName = "Now Playing" };
        _profile.Pages.Add(nowPlayingPage);
        _profile.ActivePageIndex = pageIndex;
        PlaceWidgetOnCanvas("now_playing", 0, 0, 1016, 592);

        // ── Page 3: Weather Forecast ──
        pageIndex = _profile.Pages.Count;
        var weatherPage = new PageLayout { PageName = "Weather Forecast" };
        _profile.Pages.Add(weatherPage);
        _profile.ActivePageIndex = pageIndex;
        PlaceWidgetOnCanvas("weather_forecast", 0, 0, 1016, 592);

        // ── Page 4: Twitch & Picture ──
        pageIndex = _profile.Pages.Count;
        var twitchPage = new PageLayout { PageName = "Twitch & Picture" };
        _profile.Pages.Add(twitchPage);
        _profile.ActivePageIndex = pageIndex;
        PlaceWidgetOnCanvas("twitch_chat", 0, 0, 406, 592);
        PlaceWidgetOnCanvas("picture_viewer", 406, 0, 610, 592);

        _profile.ActivePageIndex = 0;
    }

    private void PlaceWidgetOnCanvas(string pluginId, float x, float y, float width = -1, float height = -1)
    {
        var placed = new PlacedWidgetInstance
        {
            PluginId = pluginId,
            DisplayName = _loader.RegisteredPlugins.FirstOrDefault(p => p.PluginId == pluginId)?.DisplayName ?? pluginId,
            X = x,
            Y = y,
            ZIndex = _profile.ActivePage.Widgets.Count + 1
        };

        var instance = RehydrateWidget(placed);
        if (instance == null) return;

        placed.Width = width > 0 ? width : instance.DefaultSize.Width;
        placed.Height = height > 0 ? height : instance.DefaultSize.Height;

        _profile.ActivePage.Widgets.Add(placed);
        SelectWidget(placed);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    /// <summary>
    /// Creates and initializes the active widget instance for a placed widget, then applies
    /// the user-configured custom property values (surviving Export/Import round-trips).
    /// </summary>
    private ModernWidget? RehydrateWidget(PlacedWidgetInstance placed)
    {
        var instance = _loader.CreateInstance(placed.PluginId);
        if (instance == null) return null;

#pragma warning disable S6966 // Widget initialization must complete before placement — sync wrapper during startup
        instance.InitializeAsync(this).GetAwaiter().GetResult();
#pragma warning restore S6966

        var type = instance.GetType();
        foreach (var prop in type.GetProperties())
        {
            var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
            if (attr == null) continue;
            if (!placed.PropertyValues.TryGetValue(prop.Name, out object? raw)) continue;

            object? value = ConvertPropertyValue(raw, prop.PropertyType);
            if (value == null) continue;

            try
            {
                prop.SetValue(instance, value);
                instance.OnPropertyChanged(prop.Name, value);
            }
            catch
            {
                // Stored value is incompatible with the widget property type; ignore it
            }
        }

        placed.ActiveInstance = instance;
        return instance;
    }

    private static object? ConvertPropertyValue(object? raw, Type targetType)
    {
        // Imported JSON dictionaries arrive as JsonElement values; deserialize them into the real type
        if (raw is not JsonElement je) return raw;
        try
        {
            return je.Deserialize(targetType);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SelectWidget(PlacedWidgetInstance? widget)
    {
        _selectedWidget = widget;
        _compositor.SelectedWidget = widget;
        UpdateInspectorPanel();
        SkiaCanvas.InvalidateVisual();
    }

    private void UpdateActiveCount()
    {
        TxtActiveCount.Text = $"Active Widgets: {_profile.ActivePage.Widgets.Count}";
    }

    #region Skia Canvas Rendering & Mouse Interaction

    private void SkiaCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _compositor.Compose(_profile.ActivePage, 60.0f, _profile.ActivePageIndex, _profile.Pages.Count);
        e.Surface.Canvas.DrawBitmap(_compositor.FrameBuffer, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        // Frame routing is handled by _renderTimer.Tick — do NOT send here to avoid double-sending.
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var canvasPos = e.GetPosition(SkiaCanvas);
        if (canvasPos.X >= 0 && canvasPos.Y >= 0 &&
            canvasPos.X <= SkiaCanvas.ActualWidth && canvasPos.Y <= SkiaCanvas.ActualHeight)
            return;

        var inspectorPos = e.GetPosition(InspectorPanel);
        if (inspectorPos.X >= 0 && inspectorPos.Y >= 0 &&
            inspectorPos.X <= InspectorPanel.ActualWidth && inspectorPos.Y <= InspectorPanel.ActualHeight)
            return;

        SelectWidget(null);
    }

    private void SkiaCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        var pos = e.GetPosition(SkiaCanvas);
        _lastMousePos = pos;
        _swipeStartX = (float)pos.X;
        _swipeStartY = (float)pos.Y;

        // Check if user clicked on side navigation arrows when multiple pages exist
        if (_profile.Pages.Count > 1)
        {
            if (pos.X <= 50 && pos.Y >= 250 && pos.Y <= 350 && _profile.ActivePageIndex > 0)
            {
                SwitchToPage(_profile.ActivePageIndex - 1);
                _isMouseDown = false;
                return;
            }
            if (pos.X >= 974 && pos.Y >= 250 && pos.Y <= 350 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
            {
                SwitchToPage(_profile.ActivePageIndex + 1);
                _isMouseDown = false;
                return;
            }
        }

        // Hit test against active widgets
        var hit = SkiaFrameCompositor.HitTest(_profile.ActivePage, (float)pos.X, (float)pos.Y);
        SelectWidget(hit);

        if (hit != null && _compositor.IsEditMode)
        {
            // Check if click is in the resize handle (bottom-right corner)
            if (hit == _selectedWidget &&
                pos.X >= hit.X + hit.Width - ResizeHandleSize &&
                pos.Y >= hit.Y + hit.Height - ResizeHandleSize)
            {
                _isResizingWidget = true;
            }
            else if (hit.ActiveInstance is HotkeyButtonWidget hotkeyWidget &&
                     IsPointOverWidgetIcon(hotkeyWidget, hit.Width, hit.Height,
                         (float)pos.X - hit.X, (float)pos.Y - hit.Y))
            {
                _isDraggingIcon = true;
                _iconDragMoved = false;
                if (TryGetWidgetIconCenter(hotkeyWidget, hit.Width, hit.Height, out var iconCenter, out _))
                    _iconGrabOffset = new Point(iconCenter.X - ((float)pos.X - hit.X), iconCenter.Y - ((float)pos.Y - hit.Y));
            }
            else
            {
                _isDraggingWidget = true;
            }
        }
        else if (hit != null && !_compositor.IsEditMode)
        {
            // Forward touch down
            SkiaFrameCompositor.RouteTouch(_profile.ActivePage, (float)pos.X, (float)pos.Y, TouchEventType.TouchDown);
        }
    }

    private void SkiaCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown) return;
        var pos = e.GetPosition(SkiaCanvas);

        if (_isResizingWidget && _selectedWidget != null && _compositor.IsEditMode)
        {
            float newW = (float)(pos.X - _selectedWidget.X);
            float newH = (float)(pos.Y - _selectedWidget.Y);
            _selectedWidget.Width = Math.Max(40, newW);
            _selectedWidget.Height = Math.Max(30, newH);

            _lastMousePos = pos;
            UpdateInspectorTransformsOnly();
            SkiaCanvas.InvalidateVisual();
        }
        else if (_isDraggingWidget && _selectedWidget != null && _compositor.IsEditMode)
        {
            float dx = (float)(pos.X - _lastMousePos.X);
            float dy = (float)(pos.Y - _lastMousePos.Y);

            _selectedWidget.X += dx;
            _selectedWidget.Y += dy;

            _lastMousePos = pos;
            UpdateInspectorTransformsOnly();
            SkiaCanvas.InvalidateVisual();
        }
        else if (_isDraggingIcon && _selectedWidget != null &&
                 _selectedWidget.ActiveInstance is HotkeyButtonWidget iconHotkey &&
                 _compositor.IsEditMode)
        {
            float localX = (float)pos.X - _selectedWidget.X;
            float localY = (float)pos.Y - _selectedWidget.Y;
            if (TryGetWidgetIconCenter(iconHotkey, _selectedWidget.Width, _selectedWidget.Height, out var iconCenter, out float half))
            {
                float cx = Math.Clamp(localX + (float)_iconGrabOffset.X, half, _selectedWidget.Width - half);
                float cy = Math.Clamp(localY + (float)_iconGrabOffset.Y, half, _selectedWidget.Height - half);
                int newX = (int)Math.Round(cx - _selectedWidget.Width / 2f);
                int newY = (int)Math.Round(cy - _selectedWidget.Height * 0.31f);
                if (newX != iconHotkey.IconOffsetX || newY != iconHotkey.IconOffsetY)
                {
                    _iconDragMoved = true;
                    iconHotkey.IconOffsetX = newX;
                    iconHotkey.IconOffsetY = newY;
                    iconHotkey.OnPropertyChanged(nameof(HotkeyButtonWidget.IconOffsetX), newX);
                    iconHotkey.OnPropertyChanged(nameof(HotkeyButtonWidget.IconOffsetY), newY);
                    _selectedWidget.PropertyValues[nameof(HotkeyButtonWidget.IconOffsetX)] = newX;
                    _selectedWidget.PropertyValues[nameof(HotkeyButtonWidget.IconOffsetY)] = newY;
                    SkiaCanvas.InvalidateVisual();
                }
            }
        }
        else if (_selectedWidget != null && !_compositor.IsEditMode)
        {
            SkiaFrameCompositor.RouteTouch(_profile.ActivePage, (float)pos.X, (float)pos.Y, TouchEventType.TouchMove);
        }
    }

    private void SkiaCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SkiaCanvas);

        // Check for horizontal swipe gesture left/right
        float deltaX = (float)pos.X - _swipeStartX;
        float deltaY = (float)pos.Y - _swipeStartY;

        if (_profile.Pages.Count > 1 && !_isDraggingWidget && !_isDraggingIcon && Math.Abs(deltaX) > 80 && Math.Abs(deltaY) < 70)
        {
            if (deltaX < -80 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
            {
                // Swiped Left -> Next Page
                SwitchToPage(_profile.ActivePageIndex + 1);
                _isMouseDown = false;
                return;
            }
            else if (deltaX > 80 && _profile.ActivePageIndex > 0)
            {
                // Swiped Right -> Previous Page
                SwitchToPage(_profile.ActivePageIndex - 1);
                _isMouseDown = false;
                return;
            }
        }

        if (_selectedWidget != null && !_compositor.IsEditMode && _isMouseDown)
        {
            SkiaFrameCompositor.RouteTouch(_profile.ActivePage, (float)pos.X, (float)pos.Y, TouchEventType.TouchUp);
        }

        if ((_isDraggingWidget || _isResizingWidget) && _selectedWidget != null && _profile.ActivePage.SnapToGrid)
        {
            _selectedWidget.X = (float)Math.Round(_selectedWidget.X / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth;
            _selectedWidget.Y = (float)Math.Round(_selectedWidget.Y / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight;
            if (_isResizingWidget)
            {
                _selectedWidget.Width = (float)Math.Round(_selectedWidget.Width / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth;
                _selectedWidget.Height = (float)Math.Round(_selectedWidget.Height / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight;
            }
            UpdateInspectorTransformsOnly();
            SkiaCanvas.InvalidateVisual();
        }

        _isMouseDown = false;
        _isDraggingWidget = false;
        _isResizingWidget = false;
        _isDraggingIcon = false;

        if (_iconDragMoved && _selectedWidget != null)
            UpdateInspectorPanel();
        _iconDragMoved = false;
    }

    private static bool TryGetWidgetIconCenter(HotkeyButtonWidget hotkey, float width, float height, out SKPoint center, out float half)
    {
        float maxIconSize = Math.Min(width, height * 0.62f);
        float iconSize = hotkey.IconSize > 0 ? hotkey.IconSize : Math.Min(width, height) * 0.4f;
        iconSize = Math.Clamp(iconSize, 0f, maxIconSize);
        half = iconSize / 2f;
        if (half <= 0f)
        {
            center = default;
            return false;
        }
        center = new SKPoint(
            Math.Clamp(width / 2f + hotkey.IconOffsetX, half, width - half),
            Math.Clamp(height * 0.31f + hotkey.IconOffsetY, half, height - half));
        return true;
    }

    private static bool IsPointOverWidgetIcon(HotkeyButtonWidget hotkey, float width, float height, float localX, float localY)
    {
        if (string.IsNullOrWhiteSpace(hotkey.IconFile))
        {
            if (string.IsNullOrWhiteSpace(hotkey.Icon) || !GriddyIcons.Contains(hotkey.Icon))
                return false;
        }
        else if (!SvgIconLoader.TryGetPath(hotkey.IconFile, out _))
        {
            return false;
        }

        if (!TryGetWidgetIconCenter(hotkey, width, height, out var center, out float half))
            return false;

        float dx = localX - center.X;
        float dy = localY - center.Y;
        return dx * dx + dy * dy <= half * half;
    }

    #endregion

    #region Right Property Inspector Dynamic Binding

    private void UpdateInspectorPanel()
    {
        if (_selectedWidget == null)
        {
            PanelEmptyInspector.Visibility = Visibility.Visible;
            PanelActiveInspector.Visibility = Visibility.Collapsed;
            return;
        }

        _isUpdatingInspector = true;
        try
        {
            PanelEmptyInspector.Visibility = Visibility.Collapsed;
            PanelActiveInspector.Visibility = Visibility.Visible;

            TxtInspName.Text = _selectedWidget.DisplayName;

            TxtPosX.Text = $"{_selectedWidget.X:F0}";
            TxtPosY.Text = $"{_selectedWidget.Y:F0}";
            TxtWidth.Text = $"{_selectedWidget.Width:F0}";
            TxtHeight.Text = $"{_selectedWidget.Height:F0}";
            TxtZIndex.Text = $"{_selectedWidget.ZIndex}";
            TxtRotation.Text = $"{_selectedWidget.Rotation:F0}";
            SliderOpacity.Value = _selectedWidget.Opacity;
            TxtOpacityVal.Text = $"{(int)(_selectedWidget.Opacity * 100)}%";

            // Build dynamic custom property editors for the widget
            PanelCustomProperties.Children.Clear();
            if (_selectedWidget.ActiveInstance != null)
            {
                var type = _selectedWidget.ActiveInstance.GetType();
                ComboBox? actionTypeCombo = null;
                StackPanel? actionCommandPanel = null;
                foreach (var prop in type.GetProperties())
                {
                    var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
                    if (attr == null) continue;

                    if (prop.DeclaringType == typeof(HotkeyButtonWidget) &&
                        prop.Name == nameof(HotkeyButtonWidget.IconFile))
                        continue;

                    if (attr.PropertyType == WidgetPropertyType.Button)
                    {
                        var btn = new Button
                        {
                            Content = attr.DisplayName,
                            Padding = new Thickness(8, 4, 8, 4),
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(0, 0, 0, 10)
                        };
                        if (_selectedWidget.ActiveInstance is IWidgetActionPresentationProvider presentation)
                        {
                            string? label = presentation.GetWidgetActionLabel(prop.Name);
                            if (!string.IsNullOrWhiteSpace(label)) btn.Content = label;

                            if (presentation.IsWidgetActionActive(prop.Name))
                            {
                                btn.Background = TryFindResource("SuccessBackground") as Brush ?? btn.Background;
                                btn.BorderBrush = TryFindResource("SuccessBorder") as Brush ?? btn.BorderBrush;
                                btn.BorderThickness = new Thickness(1);
                                btn.Foreground = TryFindResource("M3Primary") as Brush ?? Brushes.White;
                            }
                        }
                        btn.Click += (_, _) =>
                        {
                            if (_selectedWidget?.ActiveInstance is IWidgetActionInvoker invoker)
                                invoker.InvokeWidgetAction(prop.Name);
                        };
                        PanelCustomProperties.Children.Add(btn);
                        continue;
                    }

                    var propPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                    propPanel.Children.Add(new TextBlock
                    {
                        Text = attr.DisplayName,
                        FontSize = 11,
                        Foreground = Brushes.White,
                        Margin = new Thickness(0, 0, 0, 4)
                    });

                    object? currentVal = prop.GetValue(_selectedWidget.ActiveInstance) ?? attr.DefaultValue;

                    IReadOnlyList<WidgetPropertyOption> propertyOptions = attr.Options
                        .Select(option => new WidgetPropertyOption(option, option))
                        .ToArray();
                    if (_selectedWidget.ActiveInstance is IWidgetPropertyOptionsProvider optionsProvider)
                    {
                        var dynamicOptions = optionsProvider.GetPropertyOptions(prop.Name);
                        if (dynamicOptions.Count > 0) propertyOptions = dynamicOptions;
                    }

                    if (attr.PropertyType == WidgetPropertyType.Choice && propertyOptions.Count > 0)
                    {
                        var combo = new ComboBox
                        {
                            ItemsSource = propertyOptions,
                            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
                            SelectedValuePath = nameof(WidgetPropertyOption.Value),
                            SelectedValue = currentVal?.ToString(),
                            Padding = new Thickness(8, 4, 8, 4)
                        };
                        if (prop.Name == nameof(HotkeyButtonWidget.ActionType)) actionTypeCombo = combo;
                        combo.SelectionChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            string? selectedValue = combo.SelectedValue?.ToString();
                            if (!string.IsNullOrWhiteSpace(selectedValue))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, selectedValue);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, selectedValue);
                                _selectedWidget.PropertyValues[prop.Name] = selectedValue;
                            }
                        };
                        AttachDropdownWithinWindow(combo);
                        propPanel.Children.Add(combo);
                    }
                    else if (attr.PropertyType == WidgetPropertyType.Font)
                    {
                        var combo = new ComboBox
                        {
                            ItemsSource = propertyOptions.Count > 0
                                ? propertyOptions
                                : FontCatalog.GetAllFamilies().Select(family => new WidgetPropertyOption(family, family)).ToArray(),
                            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
                            SelectedValuePath = nameof(WidgetPropertyOption.Value),
                            SelectedValue = currentVal?.ToString(),
                            Padding = new Thickness(8, 4, 8, 4)
                        };
                        combo.SelectionChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            string? selectedValue = combo.SelectedValue?.ToString();
                            if (!string.IsNullOrWhiteSpace(selectedValue))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, selectedValue);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, selectedValue);
                                _selectedWidget.PropertyValues[prop.Name] = selectedValue;
                            }
                        };
                        AttachDropdownWithinWindow(combo);
                        propPanel.Children.Add(combo);
                    }
                    else if (attr.PropertyType == WidgetPropertyType.Icon)
                    {
                        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                        var iconFileProp = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile))!;
                        string seed = currentVal?.ToString() ?? "";
                        if (string.IsNullOrEmpty(seed)
                            && _selectedWidget?.ActiveInstance is HotkeyButtonWidget hotkeySeed
                            && !string.IsNullOrEmpty(hotkeySeed.IconFile))
                        {
                            seed = hotkeySeed.IconFile;
                        }
                        var box = new TextBox { Text = seed };
                        var btnBrowse = new Button { Content = "Browse\u2026", Padding = new Thickness(8, 2, 8, 2) };
                        DockPanel.SetDock(btnBrowse, Dock.Right);
                        btnBrowse.Click += (_, _) =>
                        {
                            if (_selectedWidget?.ActiveInstance is not HotkeyButtonWidget hotkey) return;
                            ShowIconSelectorPopup(prop, hotkey, box);
                        };
                        box.TextChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            ApplyInspectorPropertyValue(iconFileProp, "");
                            ApplyInspectorPropertyValue(prop, box.Text);
                        };
                        row.Children.Add(btnBrowse);
                        row.Children.Add(box);
                        propPanel.Children.Add(row);
                    }
                    else if (attr.PropertyType == WidgetPropertyType.Boolean)
                    {
                        var chk = new CheckBox
                        {
                            Content = "Enabled / Active",
                            IsChecked = currentVal is bool b && b,
                            Foreground = Brushes.White
                        };
                        chk.Checked += (s, e) => { prop.SetValue(_selectedWidget.ActiveInstance, true); _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, true); _selectedWidget.PropertyValues[prop.Name] = true; };
                        chk.Unchecked += (s, e) => { prop.SetValue(_selectedWidget.ActiveInstance, false); _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, false); _selectedWidget.PropertyValues[prop.Name] = false; };
                        propPanel.Children.Add(chk);
                    }
                    else if (attr.PropertyType == WidgetPropertyType.Path)
                    {
                        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                        bool isHotkeyCommand = prop.DeclaringType == typeof(HotkeyButtonWidget) &&
                            prop.Name == nameof(HotkeyButtonWidget.ActionCommand);

                        var txt = new TextBox { Text = currentVal?.ToString() ?? "" };
                        txt.TextChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            ApplyInspectorPropertyValue(prop, txt.Text);
                        };

                        var btnFolder = new Button
                        {
                            Content = "Folder\u2026",
                            Padding = new Thickness(8, 2, 8, 2),
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        DockPanel.SetDock(btnFolder, Dock.Right);
                        btnFolder.Click += (s, e) =>
                        {
                            var dlg = new OpenFolderDialog { Title = isHotkeyCommand ? "Select action folder" : "Select image folder" };
                            if (dlg.ShowDialog() == true)
                            {
                                txt.Text = dlg.FolderName;
                            }
                        };

                        var btnFile = new Button
                        {
                            Content = "File\u2026",
                            Padding = new Thickness(8, 2, 8, 2),
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        DockPanel.SetDock(btnFile, Dock.Right);
                        btnFile.Click += (s, e) =>
                        {
                            var dlg = new OpenFileDialog
                            {
                                Title = isHotkeyCommand ? "Select action file or executable" : "Select image file",
                                Filter = isHotkeyCommand
                                    ? "Programs and files (*.*)|*.*"
                                    : "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"
                            };
                            if (dlg.ShowDialog() == true)
                            {
                                txt.Text = dlg.FileName;
                            }
                        };

                        row.Children.Add(btnFile);
                        row.Children.Add(btnFolder);
                        row.Children.Add(txt);
                        propPanel.Children.Add(row);
                        if (prop.Name == nameof(HotkeyButtonWidget.ActionCommand)) actionCommandPanel = propPanel;
                    }
                    else if (attr.PropertyType == WidgetPropertyType.SensorSelector)
                    {
                        var labels = LhmSensorStore.ReadSnapshot()
                            .Readings
                            .Select(r => r.Label)
                            .Distinct()
                            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (labels.Count > 0)
                        {
                            var combo = new ComboBox
                            {
                                ItemsSource = labels,
                                SelectedItem = currentVal?.ToString(),
                                Padding = new Thickness(8, 4, 8, 4)
                            };
                            combo.SelectionChanged += (s, e) =>
                            {
                                if (combo.SelectedItem != null)
                                {
                                    prop.SetValue(_selectedWidget.ActiveInstance, combo.SelectedItem.ToString());
                                    _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, combo.SelectedItem.ToString());
                                    _selectedWidget.PropertyValues[prop.Name] = combo.SelectedItem.ToString();
                                }
                            };
                            AttachDropdownWithinWindow(combo);
                            propPanel.Children.Add(combo);
                        }
                        else
                        {
                            propPanel.Children.Add(new TextBlock
                            {
                                Text = "No sensors detected. Start the ModernWigiDash service, then reopen settings to pick a sensor.",
                                FontSize = 11,
                                Foreground = (Brush)FindResource("TextSecondary"),
                                TextWrapping = TextWrapping.Wrap
                            });
                        }
                    }
                    else
                    {
                        // Text or Number or Color
                        var txt = new TextBox { Text = currentVal?.ToString() ?? "" };
                        txt.TextChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            string str = txt.Text;
                            if (prop.PropertyType == typeof(float) && float.TryParse(str, out float fVal))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, fVal);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, fVal);
                                _selectedWidget.PropertyValues[prop.Name] = fVal;
                            }
                            else if (prop.PropertyType == typeof(int) && int.TryParse(str, out int iVal))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, iVal);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, iVal);
                                _selectedWidget.PropertyValues[prop.Name] = iVal;
                            }
                            else if (prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, str);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, str);
                                _selectedWidget.PropertyValues[prop.Name] = str;
                            }
                        };
                        propPanel.Children.Add(txt);
                    }

                    PanelCustomProperties.Children.Add(propPanel);
                }

                if (actionTypeCombo != null && actionCommandPanel != null)
                {
                    void UpdateActionCommandVisibility()
                    {
                        string? selected = actionTypeCombo.SelectedValue?.ToString();
                        actionCommandPanel.Visibility =
                            selected is "Launch App" or "Open URL" ? Visibility.Visible : Visibility.Collapsed;
                    }
                    actionTypeCombo.SelectionChanged += (_, _) => UpdateActionCommandVisibility();
                    UpdateActionCommandVisibility();
                }
            }
        }
        finally
        {
            _isUpdatingInspector = false;
        }
    }

    /// <summary>
    /// Keeps a ComboBox dropdown inside the window's client area. WPF positions the
    /// popup against the screen, so a dropdown near the window's bottom edge extends
    /// below the window where its options can't be clicked. This flips the dropdown
    /// upward (or clamps it) and caps its height so every option stays inside the window.
    /// </summary>
    private static void AttachDropdownWithinWindow(ComboBox combo)
    {
        combo.Loaded += (_, _) =>
        {
            combo.ApplyTemplate();
            if (Window.GetWindow(combo) is not Window window) return;
            if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;
            if (window.Content is not Visual content) return;

            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                double clientW = (content as FrameworkElement)?.ActualWidth ?? window.ActualWidth;
                double clientH = (content as FrameworkElement)?.ActualHeight ?? window.ActualHeight;
                var tl = combo.TransformToAncestor(content).Transform(new Point(0, 0));

                var placements = new List<CustomPopupPlacement>();
                if (clientH - (tl.Y + targetSize.Height) >= popupSize.Height)
                {
                    placements.Add(new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal));
                }
                if (tl.Y >= popupSize.Height)
                {
                    placements.Add(new CustomPopupPlacement(new Point(0, -popupSize.Height), PopupPrimaryAxis.Horizontal));
                }

                double popupLeft = Math.Clamp(tl.X, 0, Math.Max(0, clientW - popupSize.Width));
                double popupTop = Math.Clamp(tl.Y + targetSize.Height, 0, Math.Max(0, clientH - popupSize.Height));
                placements.Add(new CustomPopupPlacement(new Point(popupLeft - tl.X, popupTop - tl.Y), PopupPrimaryAxis.Horizontal));
                return placements.ToArray();
            };
        };

        combo.DropDownOpened += (_, _) =>
        {
            if (Window.GetWindow(combo) is not Window window) return;
            if (window.Content is not FrameworkElement content) return;
            if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;

            var tl = combo.TransformToAncestor(content).Transform(new Point(0, 0));
            double below = content.ActualHeight - (tl.Y + combo.ActualHeight);
            double above = tl.Y;
            double available = Math.Max(120, Math.Max(below, above) - 10);

            if (popup.Child is FrameworkElement popupContent)
            {
                if (FindVisualChild<ScrollViewer>(popupContent) is ScrollViewer scroll)
                {
                    scroll.MaxHeight = available;
                }
            }
        };
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is T inner) return inner;
        }
        return null;
    }

    private void ShowIconSelectorPopup(PropertyInfo iconProp, HotkeyButtonWidget hotkey, TextBox box)
    {
        var iconFileProp = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile))!;

        var dialog = new Window
        {
            Title = "Select Icon",
            Width = 520,
            Height = 620,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgPanel") as Brush ?? TryFindResource("PanelBackground") as Brush ?? Brushes.Black,
            Foreground = Brushes.White
        };
        dialog.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var search = new TextBox { ToolTip = "Search icons by name", Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(search, 0);
        root.Children.Add(search);

        var browseSvg = new Button
        {
            Content = "Browse SVG\u2026",
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var chip = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var browseRow = new StackPanel { Orientation = Orientation.Horizontal };
        browseRow.Children.Add(browseSvg);
        browseRow.Children.Add(chip);
        Grid.SetRow(browseRow, 1);
        root.Children.Add(browseRow);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        var grid = new WrapPanel { ItemWidth = 40, ItemHeight = 40 };
        scroll.Content = grid;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var selectedName = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var select = new Button
        {
            Content = "Select",
            Padding = new Thickness(14, 5, 14, 5),
            Style = (Style)FindResource("AccentButton")
        };
        Grid.SetColumn(selectedName, 0);
        Grid.SetColumn(select, 1);
        footer.Children.Add(selectedName);
        footer.Children.Add(select);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        string chosen = "";
        void UpdateSelected(string name)
        {
            chosen = name;
            selectedName.Text = name;
        }

        void RenderGrid()
        {
            grid.Children.Clear();
            string filter = search.Text?.Trim() ?? "";
            var names = string.IsNullOrEmpty(filter)
                ? GriddyIcons.Names
                : GriddyIcons.Names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var name in names)
            {
                var cell = new Button
                {
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Tag = name,
                    ToolTip = name,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Transparent
                };
                if (GriddyIcons.TryGetPathData(name, out string? pathData))
                {
                    try
                    {
                        cell.Content = new System.Windows.Shapes.Path
                        {
                            Width = 22,
                            Height = 22,
                            Stretch = Stretch.Uniform,
                            Fill = Brushes.White,
                            Data = Geometry.Parse(pathData)
                        };
                    }
                    catch
                    {
                        cell.Content = null;
                    }
                }
                if (name.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                    cell.BorderBrush = (Brush)FindResource("AccentRed");
                cell.Click += (_, _) =>
                {
                    UpdateSelected(name);
                    foreach (var child in grid.Children.OfType<Button>())
                        child.BorderBrush = Brushes.Transparent;
                    cell.BorderBrush = (Brush)FindResource("AccentRed");
                };
                grid.Children.Add(cell);
            }
        }

        search.TextChanged += (_, _) => RenderGrid();

        browseSvg.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "Select an SVG icon", Filter = "SVG files (*.svg)|*.svg" };
            if (dlg.ShowDialog() != true) return;
            if (!SvgIconLoader.TryGetPath(dlg.FileName, out _))
            {
                MessageBox.Show(dialog, "Only single-path SVG icons are supported.", "Unsupported SVG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string relative = SvgIconLoader.CopyToIcons(dlg.FileName);
            ApplyInspectorPropertyValue(iconFileProp, relative);
            ApplyInspectorPropertyValue(iconProp, "");
            hotkey.IconFile = relative;
            hotkey.Icon = "";
            chip.Text = $"Custom: {relative}";
            _isUpdatingInspector = true;
            try
            {
                box.Text = relative;
            }
            finally
            {
                _isUpdatingInspector = false;
            }
            UpdateSelected(relative);
        };

        select.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (GriddyIcons.Contains(chosen))
            {
                ApplyInspectorPropertyValue(iconFileProp, "");
                ApplyInspectorPropertyValue(iconProp, chosen);
                hotkey.IconFile = "";
                hotkey.Icon = chosen;
                _isUpdatingInspector = true;
                try
                {
                    box.Text = chosen;
                }
                finally
                {
                    _isUpdatingInspector = false;
                }
            }
            dialog.DialogResult = true;
        };

        if (!string.IsNullOrWhiteSpace(hotkey.IconFile))
        {
            chip.Text = $"Custom: {hotkey.IconFile}";
            chosen = hotkey.IconFile;
            selectedName.Text = hotkey.IconFile;
        }
        else
        {
            chosen = hotkey.Icon;
            selectedName.Text = hotkey.Icon;
        }
        RenderGrid();
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void UpdateInspectorTransformsOnly()
    {
        if (_selectedWidget == null) return;
        _isUpdatingInspector = true;
        TxtPosX.Text = $"{_selectedWidget.X:F0}";
        TxtPosY.Text = $"{_selectedWidget.Y:F0}";
        TxtWidth.Text = $"{_selectedWidget.Width:F0}";
        TxtHeight.Text = $"{_selectedWidget.Height:F0}";
        _isUpdatingInspector = false;
    }

    private void ApplyInspectorPropertyValue(PropertyInfo prop, object value)
    {
        if (_selectedWidget?.ActiveInstance == null) return;
        prop.SetValue(_selectedWidget.ActiveInstance, value);
        _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, value);
        _selectedWidget.PropertyValues[prop.Name] = value;
    }

    private void Transform_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingInspector || _selectedWidget == null) return;

        if (float.TryParse(TxtPosX.Text, out float x)) _selectedWidget.X = x;
        if (float.TryParse(TxtPosY.Text, out float y)) _selectedWidget.Y = y;
        if (float.TryParse(TxtWidth.Text, out float w) && w > 20) _selectedWidget.Width = w;
        if (float.TryParse(TxtHeight.Text, out float h) && h > 20) _selectedWidget.Height = h;
        if (int.TryParse(TxtZIndex.Text, out int z)) _selectedWidget.ZIndex = z;
        if (float.TryParse(TxtRotation.Text, out float r)) _selectedWidget.Rotation = r % 360;

        SkiaCanvas.InvalidateVisual();
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedWidget != null && TxtOpacityVal != null)
        {
            _selectedWidget.Opacity = (float)SliderOpacity.Value;
            TxtOpacityVal.Text = $"{(int)(_selectedWidget.Opacity * 100)}%";
            SkiaCanvas.InvalidateVisual();
        }
    }

    private void BtnDeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget != null)
        {
            _profile.ActivePage.Widgets.Remove(_selectedWidget);
            SelectWidget(null);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    #endregion

    #region Catalog, Header, and Action Handlers

    private void ScrollerPageTabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollerPageTabs.ScrollToHorizontalOffset(
            ScrollerPageTabs.HorizontalOffset - e.Delta);
    }

    private void TxtSearchCatalog_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = TxtSearchCatalog.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ListCatalog.ItemsSource = _loader.RegisteredPlugins.OrderBy(p => p.DisplayName).ToList();
        }
        else
        {
            ListCatalog.ItemsSource = _loader.RegisteredPlugins
                .Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            p.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.DisplayName).ToList();
        }
    }

    private void BtnPlaceWidget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pluginId)
        {
            var instance = _loader.CreateInstance(pluginId);
            if (instance == null) return;

            var sz = instance.DefaultSize;
            // Full-screen widgets go to origin; smaller ones center on the grid
            if (sz.Width >= GridSizeExtensions.ScreenWidth - 10 || sz.Height >= GridSizeExtensions.ScreenHeight - 10)
            {
                PlaceWidgetOnCanvas(pluginId, 0, 0);
            }
            else
            {
                float cx = (float)Math.Round(GridSizeExtensions.ScreenWidth / 2.0 / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth;
                float cy = (float)Math.Round(GridSizeExtensions.ScreenHeight / 2.0 / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight;
                PlaceWidgetOnCanvas(pluginId, cx - sz.Width / 2, cy - sz.Height / 2);
            }
        }
    }

    private void RebuildPageTabsUI()
    {
        if (PanelPageTabs == null) return;
        PanelPageTabs.Children.Clear();
        for (int i = 0; i < _profile.Pages.Count; i++)
        {
            int pageIndex = i;
            var page = _profile.Pages[i];
            bool isActive = (pageIndex == _profile.ActivePageIndex);
            bool canDelete = _profile.Pages.Count > 1;

            var container = new Grid { Margin = new Thickness(3, 0, 3, 0) };

            var btn = new Button
            {
                Content = $"📄 {page.PageName}",
                Padding = new Thickness(14, 6, canDelete ? 56 : 42, 6),
                Style = isActive ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button))
            };
            btn.Click += (s, e) => SwitchToPage(pageIndex);
            container.Children.Add(btn);

            var renameBtn = new Button
            {
                Content = "✏️",
                FontSize = 10,
                ToolTip = "Rename page",
                Foreground = isActive ? Brushes.White : (Brush)FindResource("TextSecondary"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, canDelete ? 24 : 4, 0),
                Cursor = Cursors.Hand
            };
            renameBtn.Click += (s, e) => RenamePage(pageIndex);
            container.Children.Add(renameBtn);

            if (canDelete)
            {
                var closeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Foreground = isActive ? Brushes.White : (Brush)FindResource("TextSecondary"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand
                };
                closeBtn.Click += (s, e) => DeletePage(pageIndex);
                container.Children.Add(closeBtn);
            }

            PanelPageTabs.Children.Add(container);
        }

        // Auto-scroll to active tab
        if (PanelPageTabs.Children.Count > _profile.ActivePageIndex &&
            PanelPageTabs.Children[_profile.ActivePageIndex] is FrameworkElement activeTab)
        {
            activeTab.BringIntoView();
        }
    }

    private void RenamePage(int index)
    {
        if (index < 0 || index >= _profile.Pages.Count) return;
        var page = _profile.Pages[index];

        string? newName = PromptForText($"Rename Page", $"New name for '{page.PageName}':", page.PageName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        page.PageName = newName;
        RebuildPageTabsUI();
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private string? PromptForText(string title, string label, string initialValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("BgPanel"),
            FontFamily = (FontFamily)FindResource("PrimaryFont")
        };
        dialog.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(labelBlock);

        var box = new TextBox { Text = initialValue };
        Grid.SetRow(box, 1);
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(buttons, 2);
        var btnCancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var btnOk = new Button { Content = "OK", Style = (Style)FindResource("AccentButton"), IsDefault = true };
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnOk);
        root.Children.Add(buttons);

        dialog.Content = root;
        box.Focus();
        box.SelectAll();

        string? result = null;
        btnOk.Click += (_, _) =>
        {
            result = box.Text;
            dialog.DialogResult = true;
        };
        btnCancel.Click += (_, _) => dialog.DialogResult = false;

        dialog.ShowDialog();
        return result;
    }

    private void SwitchToPage(int index)
    {
        if (index < 0 || index >= _profile.Pages.Count) return;
        _profile.ActivePageIndex = index;
        RebuildPageTabsUI();

        // Auto-scroll to the newly active tab
        if (PanelPageTabs.Children.Count > index &&
            PanelPageTabs.Children[index] is FrameworkElement targetTab)
        {
            targetTab.BringIntoView();
        }

        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void DeletePage(int index)
    {
        if (_profile.Pages.Count <= 1) return;
        var targetPage = _profile.Pages[index];
        if (targetPage.Widgets.Count > 0)
        {
            var res = MessageBox.Show($"Are you sure you want to delete '{targetPage.PageName}' containing {targetPage.Widgets.Count} widget(s)?", "Delete Page", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }

        _profile.Pages.RemoveAt(index);
        if (_profile.ActivePageIndex >= _profile.Pages.Count)
        {
            _profile.ActivePageIndex = _profile.Pages.Count - 1;
        }
        RebuildPageTabsUI();
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void BtnAddPage_Click(object sender, RoutedEventArgs e)
    {
        var newPage = new PageLayout { PageName = $"Page {_profile.Pages.Count + 1}" };
        _profile.Pages.Add(newPage);
        _profile.ActivePageIndex = _profile.Pages.Count - 1;
        RebuildPageTabsUI();
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void ChkSnapToGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_profile != null && ChkSnapToGrid != null)
        {
            _profile.ActivePage.SnapToGrid = ChkSnapToGrid.IsChecked == true;
            SkiaCanvas?.InvalidateVisual();
        }
    }

    private void ChkEditMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_compositor != null && ChkEditMode != null)
        {
            _compositor.IsEditMode = ChkEditMode.IsChecked == true;
            SkiaCanvas?.InvalidateVisual();
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Display Profile (*.json)|*.json", FileName = "MyDisplayProfile.json" };
        if (dlg.ShowDialog() == true)
        {
            string json = JsonSerializer.Serialize(_profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            MessageBox.Show("Profile exported successfully!", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Display Profile (*.json)|*.json" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                string json = File.ReadAllText(dlg.FileName);
                var loaded = JsonSerializer.Deserialize<ProfileLayout>(json);
                if (loaded != null)
                {
                    _profile = loaded;
                    foreach (var page in _profile.Pages)
                    {
                        foreach (var placed in page.Widgets)
                            RehydrateWidget(placed);
                    }
                    RebuildPageTabsUI();
                    SelectWidget(null);
                    UpdateActiveCount();
                    SkiaCanvas.InvalidateVisual();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing profile: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Are you sure you want to clear all widgets from the current page?", "Confirm Clear", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _profile.ActivePage.Widgets.Clear();
            SelectWidget(null);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    #endregion

    #region ModernWigiDashContext Implementation for Telemetry & Host Services

    public string GetSetting(string key, string defaultValue = "") => defaultValue;
    public void SetSetting(string key, string value) { }
    public void LogInfo(string message) => System.Diagnostics.Debug.WriteLine($"[Display INFO] {message}");
    public void LogError(string message, Exception? ex = null) => System.Diagnostics.Debug.WriteLine($"[Display ERROR] {message}: {ex}");
    public void RequestRender() => Dispatcher.InvokeAsync(() => SkiaCanvas?.InvalidateVisual());
    public bool TryGetSensorValue(string sensorId, out float value) { value = 50f; return true; }
    public string GetSensorFormattedString(string sensorId) => "50.0";

    public void RequestInspectorRefresh()
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateInspectorPanel();
            return;
        }

        _ = Dispatcher.InvokeAsync(UpdateInspectorPanel);
    }

    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
    {
        void ShowDialog()
        {
            _deviceAuthorizationWindow?.Close();

            var window = new Window
            {
                Title = $"ModernWigiDash - {serviceName} Login",
                Width = 430,
                SizeToContent = SizeToContent.Height,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = TryFindResource("PrimaryFont") as FontFamily ?? FontFamily,
                Background = TryFindResource("BgPanel") as Brush ?? TryFindResource("PanelBackground") as Brush ?? Brushes.Black,
                Foreground = TryFindResource("TextPrimary") as Brush ?? Brushes.White
            };
            window.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(window, ThemeSettings.Theme.TitleBar);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = $"Authorize {serviceName} in your browser",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new TextBlock
            {
                Text = "The browser should open automatically. If it does not, open the verification URL below and enter this code:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var code = new TextBox
            {
                Text = userCode,
                IsReadOnly = true,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(code);
            root.Children.Add(new TextBlock
            {
                Text = verificationUri.AbsoluteUri,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = $"This code expires at {expiresAt.LocalDateTime:t}.",
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var open = new Button { Content = "Open Twitch", Padding = new Thickness(12, 5, 12, 5) };
            var copy = new Button { Content = "Copy code", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
            var close = new Button { Content = "Cancel", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
            if (TryFindResource("AccentButton") is Style accentStyle) open.Style = accentStyle;

            open.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(verificationUri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogError("Unable to open the Twitch authorization page", ex);
                }
            };
            copy.Click += (_, _) => Clipboard.SetText(userCode);
            close.Click += (_, _) => window.Close();

            buttons.Children.Add(open);
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            root.Children.Add(buttons);
            window.Content = root;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_deviceAuthorizationWindow, window)) _deviceAuthorizationWindow = null;
            };
            _deviceAuthorizationWindow = window;
            window.Show();
        }

        if (Dispatcher.CheckAccess()) ShowDialog();
        else Dispatcher.Invoke(ShowDialog);
    }

    public void CloseDeviceAuthorization()
    {
        void CloseDialog()
        {
            _deviceAuthorizationWindow?.Close();
            _deviceAuthorizationWindow = null;
        }

        if (Dispatcher.CheckAccess()) CloseDialog();
        else Dispatcher.Invoke(CloseDialog);
    }

    #endregion

    private void UpdateUsbBadge()
    {
        string text = _usbDevice.IsHardwareActive ? "WigiDash Attached" : "WigiDash Detached";
        if (TxtUsbStatus.Text != text)
        {
            TxtUsbStatus.Text = text;
            var resources = Application.Current.Resources;
            UsbStatusDot.Fill = (Brush)resources[_usbDevice.IsHardwareActive ? "AccentGreen" : "DangerBorder"];
        }
    }

    private void ApplyTheme()
    {
        ThemeManager.ApplyToApplication();

        // DropShadowEffect does not track DynamicResource — update it explicitly
        if (PreviewFrame?.Effect is DropShadowEffect shadow)
        {
            var accent = ThemeSettings.ParseColor(ThemeSettings.Theme.AccentRed);
            if (accent != null) shadow.Color = ToMediaColor(accent.Value);
        }

        ApplyDarkTitleBar(ThemeSettings.Theme.TitleBar);

        var t = ThemeSettings.Theme;
        Log($"[THEME] Applied: BgDark={t.BgDark} BgPanel={t.BgPanel} BgCard={t.BgCard} Border={t.Border} " +
            $"AccentRed={t.AccentRed} M3Primary={t.M3Primary} M3PrimaryContainer={t.M3PrimaryContainer} " +
            $"M3OnPrimaryContainer={t.M3OnPrimaryContainer} AccentGreen={t.AccentGreen} TextPrimary={t.TextPrimary} " +
            $"TextSecondary={t.TextSecondary} ControlHover={t.ControlHover} DropdownHover={t.DropdownHover} " +
            $"TitleBar={t.TitleBar} StatusBarBackground={t.StatusBarBackground} DangerBackground={t.DangerBackground} " +
            $"DangerBorder={t.DangerBorder} SuccessBackground={t.SuccessBackground} SuccessBorder={t.SuccessBorder}");
    }

    private static Color ToMediaColor(RgbaColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static void ApplyDarkTitleBarToWindow(System.Windows.Window window, string captionHex = "#0F111A")
    {
        if (window.Icon == null)
        {
            window.Icon = new System.Windows.Media.Imaging.BitmapImage(
                new System.Uri("pack://application:,,,/Resources/Logo/logo.ico"));
        }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Enable dark mode title bar (Windows 10 1809+)
        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        // Set title bar background to match app theme (Windows 11+)
        var color = ThemeSettings.ParseColor(captionHex) ?? new RgbaColor(255, 0x0F, 0x11, 0x1A);
        int colorRef = (color.B << 16) | (color.G << 8) | color.R; // COLORREF (BBGGRR)
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }

    private void ApplyDarkTitleBar(string captionHex = "#0F111A")
    {
        ApplyDarkTitleBarToWindow(this, captionHex);
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ShowThemeDialog();
    }

    private void ShowThemeDialog()
    {
        var dialog = new Window
        {
            Title = "🎨 Theme Customization",
            Width = 440,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)Application.Current.Resources["BgPanel"],
            FontFamily = (FontFamily)Application.Current.Resources["PrimaryFont"]
        };
        dialog.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // fields
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

        var title = new TextBlock
        {
            Text = "Chrome Theme — colors outside the widget canvas",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        var fields = new StackPanel();
        var entries = new List<(string Key, TextBox Box)>();

        var props = typeof(ThemeSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .OrderBy(p => ThemeSettings.Groups.TryGetValue(p.Name, out var group) ? group : p.Name)
            .ThenBy(p => p.Name);

        string currentGroup = "";
        foreach (var prop in props)
        {
            string group = ThemeSettings.Groups.TryGetValue(prop.Name, out var grp) ? grp : "Other";
            if (group != currentGroup)
            {
                currentGroup = group;
                fields.Children.Add(new TextBlock
                {
                    Text = group.ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources["M3Primary"],
                    Margin = new Thickness(0, 8, 0, 6)
                });
            }

            string current = (string?)prop.GetValue(ThemeSettings.Theme) ?? "#000000";
            string friendly = ThemeSettings.FriendlyName(prop.Name);
            string desc = ThemeSettings.Descriptions.TryGetValue(prop.Name, out var d) ? d : "";

            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var label = new TextBlock
            {
                Text = friendly,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = $"{friendly} ({prop.Name})"
            };
            var hint = new TextBlock
            {
                Text = desc,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            var box = new TextBox { Text = current };
            row.Children.Add(label);
            row.Children.Add(hint);
            row.Children.Add(box);
            fields.Children.Add(row);
            entries.Add((prop.Name, box));
        }
        scroll.Content = fields;
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(buttons, 2);
        var btnReset = new Button { Content = "Reset", Margin = new Thickness(0, 0, 8, 0) };
        var btnCancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        var btnApply = new Button { Content = "Apply", Style = (Style)Application.Current.Resources["AccentButton"] };

        Brush borderBrush = (Brush)Application.Current.Resources["BorderBrush"];
        void Validate()
        {
            bool valid = true;
            foreach (var (_, box) in entries)
            {
                bool ok = ThemeSettings.ParseColor(box.Text) != null;
                box.BorderBrush = ok ? borderBrush : Brushes.Red;
                box.ToolTip = ok ? null : "Enter a hex color like #RRGGBB or #AARRGGBB";
                if (!ok) valid = false;
            }
            btnApply.IsEnabled = valid;
        }

        foreach (var (_, box) in entries)
        {
            box.TextChanged += (_, _) => Validate();
            box.LostFocus += (_, _) => Validate();
        }

        btnReset.Click += (_, _) =>
        {
            var defaults = new ThemeSettings();
            foreach (var (key, box) in entries)
                box.Text = (string?)defaults.GetType().GetProperty(key)?.GetValue(defaults) ?? "#000000";
        };

        btnCancel.Click += (_, _) => dialog.Close();

        btnApply.Click += (_, _) => ApplyThemeFromDialog(entries, dialog);

        dialog.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && btnApply.IsEnabled)
            {
                ApplyThemeFromDialog(entries, dialog);
                e.Handled = true;
            }
        };

        buttons.Children.Add(btnReset);
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnApply);
        root.Children.Add(buttons);

        dialog.Content = root;
        Validate();
        dialog.ShowDialog();
    }

    private void ApplyThemeFromDialog(List<(string Key, TextBox Box)> entries, Window dialog)
    {
        foreach (var (key, box) in entries)
        {
            string value = box.Text.Trim();
            if (ThemeSettings.ParseColor(value) != null)
                ThemeSettings.Theme.GetType().GetProperty(key)?.SetValue(ThemeSettings.Theme, value);
        }
        if (!ThemeSettings.Save())
        {
            MessageBox.Show("Could not write app_theme.json next to the app. The colors will apply for this session only.",
                            "Theme Save Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ApplyTheme();
        dialog.Close();
    }

    private static void Log(string msg)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
        catch (IOException)
        {
            // Log file may be locked or unavailable; silently ignore
        }
    }
}
