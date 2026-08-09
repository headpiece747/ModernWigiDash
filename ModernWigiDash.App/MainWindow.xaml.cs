using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Hardware.Transport;

using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ModernWigiDash.App;

public partial class MainWindow : Window, IModernWigiDashContext
{
    private readonly WidgetPluginLoader _loader = new();
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly DisplayDeviceEngine _usbDevice = new();

    /// <summary>Owns the 30 FPS compose→send→repaint cadence (see <see cref="FramePump"/>).</summary>
    private readonly FramePump _framePump;

    // Poll loops — one parameterized loop module per producer: SENSOR (LHS
    // shared memory, ADR-0004) and FRAMETIME (PresentMon, ADR-0003) are direct
    // producers started immediately.
    private readonly Sdk.PollLoop _sensorPoll;
    // PresentMon frame-time producer (ADR-0003) — polls the PresentMon
    // Service directly, independent of service routing. Injected so the
    // window can be constructed with a fake in tests (no real DLL load).
    private readonly IPresentMonNative _presentMonNative;
    private readonly PresentMonFrameTimeProducer _presentMonProducer;
    private readonly Sdk.PollLoop _frameTimePoll;

    // LibreHardwareService sensor producer (ADR-0004) — reads the named
    // shared-memory maps directly, independent of service routing.
    private readonly LhmSharedMemoryReader _lhsReader = new();

    private ProfileLayout _profile;
    private PlacedWidgetInstance? _selectedWidget;

    // XAML-fired events can arrive during InitializeComponent, before the ctor
    // assigns the modules they forward to (e.g. the opacity slider's initial
    // ValueChanged). Guarded handlers no-op until this is set, as the last
    // ctor statement.
    private bool _wired;

#pragma warning disable S125 // input-handling documentation, not commented-out code
    // Mouse & Swipe Gesture Interaction State. The gesture machine, its outcome
    // application, and edit-mode manipulation decisions live in InputController;
    // MainWindow only tracks button state and drives UI refresh.
#pragma warning restore S125
    private bool _isMouseDown = false;
    private readonly Input.InputController _inputController;

    // Frame presentation — decouple the UI render timer from transport
    // round-trips: one DisplayPresenter (a FrameDelivery with the single
    // encode→pool→coalesce→pace policy and the 33ms pacing the engine's USB
    // writes used) bound to the direct-USB engine (ADR-0005).
    private readonly DisplayPresenter _presenter;

    // Deep modules: the property inspector, the small host dialogs, and the
    // default profile builder own their logic; the window keeps wiring.
    private readonly Inspector.InspectorController _inspector;
    private readonly DialogHost _dialogHost;
    private readonly StarterProfile _starterProfile;

    public MainWindow()
        : this(new PresentMonNative())
    {
    }

    /// <summary>Test seam: the native PresentMon interop is injected so window
    /// construction never loads the real DLL in the test host.</summary>
    internal MainWindow(IPresentMonNative presentMonNative)
    {
        _presentMonNative = presentMonNative;

        // The engine is inert until Start: construction never probes USB, the
        // window's field initializer only allocates. Start the background
        // connect + touch poll explicitly.
        _usbDevice.Start();
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTheme();
        PreviewMouseDown += OnWindowPreviewMouseDown;

        _presenter = new DisplayPresenter(
            _usbDevice.SendFrameBytes,
            () => _usbDevice.State == ConnectionState.Connected,
            msg => FileLog.Write("[HW] " + msg));

        // One poll loop per direct producer.
        _sensorPoll = new Sdk.PollLoop(
            "SENSOR", TimeSpan.FromSeconds(1), () => true, SensorPollTick, () => { }, msg => Log(msg));

        // PresentMon frame-time poll (ADR-0003): direct, started immediately,
        // gated only on the runtime-loaded API library being available. The
        // producer owns its tracking-target resolution (foreground process +
        // descendants) and process-name lookup.
        _presentMonProducer = new PresentMonFrameTimeProducer(_presentMonNative, new TrackedTargetResolver());
        _frameTimePoll = new Sdk.PollLoop(
            "FRAMETIME", TimeSpan.FromSeconds(1), () => _presentMonNative.IsAvailable, FrameTimePollTick, () => { }, msg => Log(msg));
        _frameTimePoll.Start();
        _sensorPoll.Start();

        // The engine's Start() above fires the initial connect.
        // Do NOT block the UI thread waiting for USB — the render timer will
        // start sending frames as soon as the connection succeeds.

        // 1. Register Built-In Display Suite (attribute-driven catalog — adding a
        //    widget to the Widgets assembly needs no registration here)
        _loader.RegisterBuiltInAssembly(typeof(DigitalAnalogClockWidget).Assembly);

        // 2. Populate Catalog UI (sorted alphabetically by display name)
        RefreshCatalog();

        // 3. Build the host modules (input, inspector, dialog host) BEFORE the
        // starter profile. Widget InitializeAsync runs synchronously inside
        // _starterProfile.Create() and may call back into the context (e.g.
        // Twitch's RestoreTwitchSessionAsync -> RequestInspectorRefresh /
        // ShowDeviceAuthorization). Constructing the modules first removes the
        // startup NRE when those callbacks arrive before the modules exist.
        // Single input module: gesture machine + outcome application + edit-mode
        // manipulation. All input sources feed it; page-switch UI work stays here.
        _inputController = new Input.InputController(
            navigateTo: SwitchToPage,
            requestRender: () => SkiaCanvas.InvalidateVisual());

        // One stateful DialogHost for the whole window: the inspector receives
        // this instance (it must never build its own — a second instance could
        // never show the device-authorization window it owns).
        _dialogHost = new DialogHost(this, TryFindResource, LogError);

        _inspector = new Inspector.InspectorController(new Inspector.InspectorControllerHost(
            owner: this,
            emptyPanel: PanelEmptyInspector,
            activePanel: PanelActiveInspector,
            nameText: TxtInspName,
            posX: TxtPosX,
            posY: TxtPosY,
            widthText: TxtWidth,
            heightText: TxtHeight,
            zIndexText: TxtZIndex,
            rotationText: TxtRotation,
            opacitySlider: SliderOpacity,
            opacityValueText: TxtOpacityVal,
            customProperties: PanelCustomProperties,
            tryFindResource: TryFindResource,
            getSelectedWidget: () => _selectedWidget,
            requestCanvasRender: () => SkiaCanvas.InvalidateVisual()),
            _dialogHost);

        // 4. Setup Default Profile Layout with 3 cool starter widgets
        _starterProfile = new StarterProfile(_loader, this);
        _profile = _starterProfile.Create();
        RebuildPageTabsUI();

        // 5. Hook up USB Hardware physical touch events to Skia RouteTouch & hardware page swipe navigation.
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

        // 5. Start 30 FPS Skia Render Loop & Hardware Frame Streamer. The pump
        // composes + sends once per tick, then repaints so the window draws
        // the same buffer it sent; the badge refresh rides the tick.
        _framePump = new FramePump(
            composeAndSend: () =>
            {
                _compositor.Compose(_profile.ActivePage);
                _presenter.Send(_compositor.FrameBuffer);
            },
            requestRepaint: () => SkiaCanvas.InvalidateVisual(),
            onTick: UpdateUsbBadge);
        _framePump.Start(); // Start the render loop immediately

        // 6. Clean lifecycle shutdown on window close / debugging stop
        Closed += (s, e) =>
        {
            _framePump.Stop();
            _framePump.Dispose();
            _sensorPoll.Stop();
            _frameTimePoll.Stop();
            _presentMonProducer.Dispose();
            _presenter.Dispose();
            ProfileOps.DisposeProfile(_profile);

            _dialogHost.CloseDeviceAuthorization();
            _compositor.Dispose();
            _usbDevice.Dispose();
        };

        // Update USB badge
        UpdateUsbBadge();
        UpdateActiveCount();
        _inspector.Refresh();

        _wired = true;
    }

    private void SelectWidget(PlacedWidgetInstance? widget)
    {
        _selectedWidget = widget;
        _compositor.SelectedWidget = widget;
        _inspector.Refresh();
        SkiaCanvas.InvalidateVisual();
    }

    private void UpdateActiveCount()
    {
        TxtActiveCount.Text = $"Active Widgets: {_profile.ActivePage.Widgets.Count}";
    }

    /// <summary>Clears the widget selection and refreshes the count and canvas.</summary>
    private void ClearSelectionAndRefresh()
    {
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    #region Skia Canvas Rendering & Mouse Interaction

    private void SkiaCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        // Pure draw: the FramePump composed this buffer and queued it for
        // delivery on this tick, so what is drawn is exactly what was sent.
        e.Surface.Canvas.DrawBitmap(_compositor.FrameBuffer, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
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
                _inspector.RefreshTransforms();
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
            _inspector.RefreshTransforms();
            SkiaCanvas.InvalidateVisual();
        }

        if (iconMoved)
            _inspector.Refresh();
    }

    #endregion

    #region Inspector event forwarding (logic lives in Inspector.InspectorController)

    private void Transform_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_wired) return;
        _inspector.TransformChanged(sender, e);
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_wired) return;
        _inspector.OpacityChanged(sender, e);
    }

    #endregion

    #region Widget Selection & Deletion

    private void BtnDeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedWidget();
    }

    /// <summary>Single delete path shared by the inspector button and the Delete/Back key.</summary>
    private void DeleteSelectedWidget()
    {
        if (_selectedWidget != null)
        {
            ProfileOps.RemoveWidget(_profile.ActivePage, _selectedWidget);
            ClearSelectionAndRefresh();
        }
    }

    /// <summary>
    /// Delete/Back removes the selected widget — except while typing in an
    /// inspector text box, where Backspace must edit the field, not delete.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Delete || e.Key == Key.Back) &&
            Keyboard.FocusedElement is not TextBox)
        {
            DeleteSelectedWidget();
            e.Handled = true;
        }
    }

    #endregion

    #region Catalog, Header, and Action Handlers

    private void ScrollerPageTabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollerPageTabs.ScrollToHorizontalOffset(
            ScrollerPageTabs.HorizontalOffset - e.Delta);
    }

    private void TxtSearchCatalog_TextChanged(object sender, TextChangedEventArgs e) => RefreshCatalog();

    /// <summary>One catalog sort, three call sites: the initial fill, the
    /// filter box, and the empty-query reset all render through this.</summary>
    private void RefreshCatalog()
    {
        string query = TxtSearchCatalog.Text.Trim();
        IEnumerable<PluginInfo> source = _loader.RegisteredPlugins;
        if (!string.IsNullOrEmpty(query))
        {
            source = source.Where(p =>
                p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        ListCatalog.ItemsSource = source.OrderBy(p => p.DisplayName).ToList();
    }

    private void BtnPlaceWidget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pluginId)
        {
            var placed = ProfileOps.PlaceCentered(_profile, _loader, this, pluginId);
            if (placed == null) return;

            SelectWidget(placed);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    private void RebuildPageTabsUI()
    {
        PanelPageTabs.Children.Clear();
        foreach (var tab in PageTabsViewModel.Build(_profile))
        {
            bool isActive = tab.IsActive;
            bool canDelete = tab.CanDelete;

            var container = new Grid { Margin = new Thickness(3, 0, 3, 0) };

            var btn = new Button
            {
                Content = $"📄 {tab.PageName}",
                Padding = new Thickness(14, 6, canDelete ? 56 : 42, 6),
                Style = isActive ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button))
            };
            btn.Click += (s, e) => SwitchToPage(tab.Index);
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
            renameBtn.Click += (s, e) => RenamePage(tab.Index);
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
                closeBtn.Click += (s, e) => DeletePage(tab.Index);
                container.Children.Add(closeBtn);
            }

            PanelPageTabs.Children.Add(container);
        }

        ScrollToPage(_profile.ActivePageIndex);
    }

    /// <summary>Brings the page tab at the given index into view.</summary>
    private void ScrollToPage(int index)
    {
        if (PanelPageTabs.Children.Count > index &&
            PanelPageTabs.Children[index] is FrameworkElement targetTab)
        {
            targetTab.BringIntoView();
        }
    }

    private void RenamePage(int index)
    {
        if (index < 0 || index >= _profile.Pages.Count) return;
        var page = _profile.Pages[index];

        string? newName = _dialogHost.PromptForText($"Rename Page", $"New name for '{page.PageName}':", page.PageName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        ProfileOps.RenamePage(page, newName);
        RebuildPageTabsUI();
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void SwitchToPage(int index)
    {
        if (!ProfileOps.SetActivePageIndex(_profile, index)) return;
        RebuildPageTabsUI();

        ScrollToPage(index);

        ClearSelectionAndRefresh();
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
        ClearSelectionAndRefresh();
    }

    private void BtnAddPage_Click(object sender, RoutedEventArgs e)
    {
        ProfileOps.AddPage(_profile);
        RebuildPageTabsUI();
        ClearSelectionAndRefresh();
    }

    private void ChkSnapToGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (!_wired) return;
        _profile.ActivePage.SnapToGrid = ChkSnapToGrid.IsChecked == true;
        SkiaCanvas.InvalidateVisual();
    }

    private void ChkEditMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_wired) return;
        _compositor.IsEditMode = ChkEditMode.IsChecked == true;
        SkiaCanvas.InvalidateVisual();
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
                    ProfileOps.DisposeProfile(_profile);
                    _profile = loaded;
                    RebuildPageTabsUI();
                    ClearSelectionAndRefresh();
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
            ClearSelectionAndRefresh();
        }
    }

    #endregion


    private string _lastUsbBadgeBrush = "";

    private void UpdateUsbBadge()
    {
        string brushKey = _usbDevice.State switch
        {
            ConnectionState.Connected => "AccentGreen",
            ConnectionState.Simulated => "AccentRed", // amber — running without the device
            _ => "DangerBorder"
        };
        if (brushKey == _lastUsbBadgeBrush) return; // state unchanged — skip the per-tick resource lookup
        _lastUsbBadgeBrush = brushKey;

        var resources = Application.Current.Resources;
        UsbStatusDot.Fill = (Brush)resources[brushKey];
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

        WindowChrome.ApplyDarkTitleBar(this, ThemeSettings.Theme.TitleBar);

        var t = ThemeSettings.Theme;
        Log($"[THEME] Applied: TitleBar={t.TitleBar} AccentRed={t.AccentRed}");
    }

    private static Color ToMediaColor(RgbaColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ShowThemeDialog();
    }

    private void ShowThemeDialog()
    {
        new Dialogs.ThemeDialog(this, ApplyTheme, WindowChrome.ApplyDarkTitleBar).ShowDialog();
    }

    private static void Log(string msg) => FileLog.Write(msg);
}
