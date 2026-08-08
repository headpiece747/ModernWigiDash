using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernWigiDash.App.FrameSinks;
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

public partial class MainWindow : Window, IModernWigiDashContext
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
    /// App↔service routing truth: readiness, poll-failure counting, and the
    /// throttled re-detect trigger (see <see cref="ServiceRouting.ServiceRoutingState"/>).
    /// </summary>
    private readonly ServiceRouting.ServiceRoutingState _routingState;

    // WCF poll loops — one parameterized loop module per producer (touch,
    // sensor, frame-time). Constructed in the ctor, started on connect.
    private readonly Sdk.PollLoop _touchPoll;
    private readonly Sdk.PollLoop _sensorPoll;
    private readonly Sdk.PollLoop _frameTimePoll;

    private ProfileLayout _profile = new();
    private PlacedWidgetInstance? _selectedWidget;
    private Window? _deviceAuthorizationWindow;

    // Mouse & Swipe Gesture Interaction State. The gesture machine, its outcome
    // application, and edit-mode manipulation decisions live in InputController;
    // MainWindow only tracks button state and drives UI refresh.
    private bool _isMouseDown = false;
    private readonly Input.InputController _inputController;
    private bool _isUpdatingInspector = false;

    // Async frame pipeline — decouple UI render timer from transport round-trips.
    // Both transports are FrameDelivery instances (the single encode→pool→
    // coalesce policy); the router picks the first-ready one per render tick.
    // The WCF instance does not pace (the pipe round-trip already bounds it and
    // pacing would add display-visible latency to page switches); the direct-USB
    // instance keeps the 33ms default the engine used to pace USB writes.
    private readonly FrameDelivery _wcfSink = new(
        encoder: new SkiaRgb565Encoder(),
        pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4),
        minInterval: TimeSpan.Zero,
        log: msg => FileLog.Write("[WCF] " + msg));
    private readonly FrameDelivery _usbSink;
    private readonly FrameSinkRouter _frameSinkRouter;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTheme();
        PreviewMouseDown += OnWindowPreviewMouseDown;

        _usbSink = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4),
            send: _usbDevice.SendFrameBytes,
            isReady: () => _usbDevice.IsConnected && !_usbDevice.IsSimulationMode,
            log: msg => FileLog.Write("[HW] " + msg));
        _frameSinkRouter = new FrameSinkRouter(
            _wcfSink,
            _usbSink,
            retryTrigger: TryRetryServiceRouting,
            isHardwareActive: () => _usbDevice.IsHardwareActive);

        // App↔service wiring: routing truth + one poll loop per producer.
        _routingState = new ServiceRouting.ServiceRoutingState(
            onReconnect: TryRetryServiceRouting,
            log: msg => Log(msg));
        _touchPoll = new Sdk.PollLoop(
            "TOUCH", TimeSpan.FromMilliseconds(16), ServiceReady, TouchPollTick, _routingState.ReportFailure, msg => Log(msg));
        _sensorPoll = new Sdk.PollLoop(
            "SENSOR", TimeSpan.FromSeconds(1), ServiceReady, SensorPollTick, _routingState.ReportFailure, msg => Log(msg));
        _frameTimePoll = new Sdk.PollLoop(
            "FRAME", TimeSpan.FromSeconds(1), ServiceReady, FrameTimePollTick, _routingState.ReportFailure, msg => Log(msg));

        // Detect if Windows Service is running and initialize WCF client for frame routing.
        // Fire-and-forget async to avoid blocking the UI thread during port detection.
        _ = InitializeWcfRoutingAsync();

        // Connection is already fired async by DisplayDeviceEngine constructor.
        // Do NOT block the UI thread waiting for USB — the render timer will
        // start sending frames as soon as the background connection succeeds.

        // 1. Register Built-In Display Suite (attribute-driven catalog — adding a
        //    widget to the Widgets assembly needs no registration here)
        _loader.RegisterBuiltInAssembly(typeof(DigitalAnalogClockWidget).Assembly);

        // 2. Populate Catalog UI (sorted alphabetically by display name)
        ListCatalog.ItemsSource = _loader.RegisteredPlugins.OrderBy(p => p.DisplayName).ToList();

        // 3. Setup Default Profile Layout with 3 cool starter widgets
        SetupDefaultStarterLayout();
        RebuildPageTabsUI();

        // Single input module: gesture machine + outcome application + edit-mode
        // manipulation. All input sources feed it; page-switch UI work stays here.
        _inputController = new Input.InputController(
            navigateTo: SwitchToPage,
            requestRender: () => SkiaCanvas.InvalidateVisual());

        // 4. Hook up USB Hardware physical touch events to Skia RouteTouch & hardware page swipe navigation.
        // Display touches are runtime input: they always route to widgets (hotkeys
        // fire on the device even while the desktop is in edit mode) — only the
        // mouse path carries the desktop edit-mode veto.
        _usbDevice.OnTouchEvent += (point, touchType) =>
        {
            Dispatcher.Invoke(() => _inputController.Feed(
                touchType, point.X, point.Y,
                suppressWidgetRouting: false,
                _profile.Pages.Count, _profile.ActivePageIndex, _profile.ActivePage));
        };

        // 5. Start 30 FPS Skia Render Loop & Hardware Frame Streamer
        _renderTimer.Interval = TimeSpan.FromMilliseconds(33.3); // 30 FPS
        _renderTimer.Tick += (s, e) =>
        {
            // Composition happens once per paint in SkiaCanvas_PaintSurface;
            // the timer only drives repaints and frame sends. Composing here
            // too would double-advance widget history/animations per frame.
            SkiaCanvas.InvalidateVisual();

            // Route frame to the first ready sink (WCF > direct USB). When no
            // sink can route and the engine yielded to a service, the router
            // triggers throttled WCF re-detection so frames aren't dropped forever.
            _frameSinkRouter.Send(_compositor.FrameBuffer);

            UpdateUsbBadge();
        };
        _renderTimer.Start(); // Start the render loop immediately

        // 6. Clean lifecycle shutdown on window close / debugging stop
        Closed += (s, e) =>
        {
            _renderTimer.Stop();
            _touchPoll.Stop();
            _sensorPoll.Stop();
            _frameTimePoll.Stop();
            _frameSinkRouter.Dispose();

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
        var placed = ProfileOps.PlaceWidget(_profile, _loader, this, pluginId, x, y, width, height);
        if (placed == null) return;

        SelectWidget(placed);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
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

        // Hit test against active widgets
        var hit = SkiaFrameCompositor.HitTest(_profile.ActivePage, (float)pos.X, (float)pos.Y);
        SelectWidget(hit);

        // Edit-mode manipulation (resize / icon-drag / widget-drag) is decided
        // inside the input controller; a non-manipulating press feeds the
        // shared gesture machine (page navigation + widget touch routing). The
        // mouse carries the edit-mode veto: authoring presses manipulate.
        var kind = _inputController.Begin(hit, _selectedWidget, (float)pos.X, (float)pos.Y, _compositor.IsEditMode);
        if (kind == Input.ManipulationKind.None)
        {
            _inputController.Feed(TouchEventType.TouchDown, (float)pos.X, (float)pos.Y,
                _compositor.IsEditMode, _profile.Pages.Count, _profile.ActivePageIndex, _profile.ActivePage);
        }
    }

    private void SkiaCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown) return;
        var pos = e.GetPosition(SkiaCanvas);

        // A manipulation consumes the sample; otherwise it feeds the machine.
        if (_inputController.Move(_selectedWidget, (float)pos.X, (float)pos.Y, _compositor.IsEditMode, out bool changed))
        {
            if (changed)
            {
                UpdateInspectorTransformsOnly();
                SkiaCanvas.InvalidateVisual();
            }
            return;
        }

        _inputController.Feed(TouchEventType.TouchMove, (float)pos.X, (float)pos.Y,
            _compositor.IsEditMode, _profile.Pages.Count, _profile.ActivePageIndex, _profile.ActivePage);
    }

    private void SkiaCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SkiaCanvas);

        // A manipulation gesture never reaches the gesture machine — it stays
        // wholly in the input controller (resize / drag / icon-drag).
        bool wasManipulating = _inputController.End(_selectedWidget, _compositor.IsEditMode, _profile.ActivePage.SnapToGrid, out bool iconMoved);
        if (!wasManipulating && _isMouseDown)
        {
            _inputController.Feed(TouchEventType.TouchUp, (float)pos.X, (float)pos.Y,
                _compositor.IsEditMode, _profile.Pages.Count, _profile.ActivePageIndex, _profile.ActivePage);
        }

        _isMouseDown = false;

        if (wasManipulating)
        {
            UpdateInspectorTransformsOnly();
            SkiaCanvas.InvalidateVisual();
        }

        if (iconMoved)
            UpdateInspectorPanel();
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
                Inspector.InspectorPanelRenderer.Render(
                    _selectedWidget,
                    Inspector.InspectorModelBuilder.Describe(_selectedWidget),
                    PanelCustomProperties.Children,
                    () => _isUpdatingInspector,
                    new Inspector.InspectorCallbacks
                    {
                        TryFindResource = name => TryFindResource(name),
                        ApplyInspectorPropertyValue = ApplyInspectorPropertyValue,
                        ShowIconSelectorPopup = ShowIconSelectorPopup,
                        AttachDropdownWithinWindow = AttachDropdownWithinWindow,
                        BrowseFile = (title, filter) =>
                        {
                            var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
                            return dlg.ShowDialog() == true ? dlg.FileName : null;
                        },
                        BrowseFolder = title =>
                        {
                            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
                            return dlg.ShowDialog() == true ? dlg.FolderName : null;
                        }
                    });
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

                List<CustomPopupPlacement> placements = [];
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
        PropertyInfo? iconFileProp = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile));

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

    private void ApplyInspectorPropertyValue(PropertyInfo? prop, object value)
    {
        if (_selectedWidget?.ActiveInstance == null || prop == null) return;

        object? converted = value;
        // TextBox input arrives as string; convert to the property's CLR type
        // so a Number/Color/etc. property is never silently dropped by a
        // SetValue type mismatch.
        if (value is string str && prop.PropertyType != typeof(string))
        {
            try
            {
                converted = TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(str);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Inspector value '{str}' not convertible to {prop.PropertyType.Name} for {prop.Name}: {ex.Message}");
                return;
            }
        }

        prop.SetValue(_selectedWidget.ActiveInstance, converted);
        _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, converted);
        _selectedWidget.PropertyValues[prop.Name] = converted;
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

        ProfileOps.RenamePage(page, newName);
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

        if (!ProfileOps.DeletePage(_profile, index)) return;
        RebuildPageTabsUI();
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void BtnAddPage_Click(object sender, RoutedEventArgs e)
    {
        ProfileOps.AddPage(_profile);
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
            string json = ProfileOps.ExportJson(_profile);
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
                var loaded = ProfileOps.ImportJson(json, _loader, this);
                if (loaded != null)
                {
                    _profile = loaded;
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
            ProfileOps.ClearPage(_profile.ActivePage);
            SelectWidget(null);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    #endregion


    private bool _lastUsbBadgeActive;

    private void UpdateUsbBadge()
    {
        bool active = _usbDevice.IsHardwareActive;
        if (active == _lastUsbBadgeActive) return; // state unchanged — skip the per-tick resource lookup
        _lastUsbBadgeActive = active;

        var resources = Application.Current.Resources;
        UsbStatusDot.Fill = (Brush)resources[active ? "AccentGreen" : "DangerBorder"];
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
        new Dialogs.ThemeDialog(this, ApplyTheme, ApplyDarkTitleBarToWindow).ShowDialog();
    }

    private static void Log(string msg) => FileLog.Write(msg);
}
