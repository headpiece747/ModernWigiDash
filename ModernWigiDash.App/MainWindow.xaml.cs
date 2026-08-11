using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Rendering;
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

    /// <summary>Owns the telemetry producers (sensor + frame-time poll loops).
    /// The PresentMon interop is injected as a ctor parameter so the window
    /// can be constructed with a fake in tests (no real DLL load).</summary>
    private readonly TelemetryProducers _telemetry;

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

    // Deep modules: the property inspector, the small host dialogs, the page
    // tabs strip, and the default profile builder own their logic; the window
    // keeps wiring.
    private readonly Inspector.InspectorController _inspector;
    private readonly DialogHost _dialogHost;
    private readonly PageTabsView _pageTabs;

    // Profile persistence: loads the saved profile at startup and owns the
    // debounced save of the current profile (assigned in the ctor before the
    // profile is loaded).
    private ProfilePersistence _profilePersistence;

    // Theme application: resources + preview shadow + per-window DWM chrome +
    // the applied-log line, all behind one seam (ThemeApplicator).
    private readonly IThemeApplicator _themeApplicator = new ThemeApplicator();

    /// <summary>Sampling options for the preview draw — hoisted so the per-frame
    /// paint never allocates a new instance.</summary>
    private static readonly SKSamplingOptions FrameSamplingOptions = new(SKFilterMode.Linear);

    public MainWindow()
        : this(new PresentMonNative())
    {
    }

    /// <summary>Test seam: the native PresentMon interop is injected so window
    /// construction never loads the real DLL in the test host.</summary>
    internal MainWindow(IPresentMonNative presentMonNative)
    {
        // The engine is inert until Start: construction never probes USB, the
        // window's field initializer only allocates. Start the background
        // connect + touch poll explicitly.
        _usbDevice.Start();
        InitializeComponent();
        SourceInitialized += (_, _) => _themeApplicator.Apply(this);
        PreviewMouseDown += OnWindowPreviewMouseDown;

        _presenter = new DisplayPresenter(
            _usbDevice.SendFrameBytes,
            () => _usbDevice.State == ConnectionState.Connected,
            msg => FileLog.Write("[HW] " + msg));

        // One poll loop per direct producer, owned by the telemetry module:
        // SENSOR (LHS shared memory, ADR-0004) and FRAMETIME (PresentMon,
        // ADR-0003) start immediately and stop on close.
        _telemetry = new TelemetryProducers(presentMonNative, Log);
        _telemetry.Start();

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
        // starterProfile.Create() and may call back into the context (e.g.
        // Twitch's RestoreTwitchSessionAsync -> RequestInspectorRefresh /
        // ShowDeviceAuthorization). Constructing the modules first removes the
        // startup NRE when those callbacks arrive before the modules exist.
        // Single input module: gesture machine + outcome application + edit-mode
        // manipulation + the press orchestration. All input sources cross its
        // source-aware surface; page-switch UI work stays here.
        _inputController = new Input.InputController(
            // _profile is assigned below (starter profile) before any input
            // event can run — the lambda only dereferences it at invoke time.
            () => new Input.InputState(_profile!.ActivePage, _profile.Pages.Count, _profile.ActivePageIndex),
            navigateTo: SwitchToPage,
            requestRender: () => SkiaCanvas.InvalidateVisual(),
            select: SelectWidget);

        // One stateful DialogHost for the whole window: the inspector receives
        // this instance (it must never build its own — a second instance could
        // never show the device-authorization window it owns).
        _dialogHost = new DialogHost(this, _themeApplicator, TryFindResource, LogError);

        _inspector = new Inspector.InspectorController(new Inspector.InspectorControllerHost(
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

        // Page-tabs strip module: owns tab construction, the wheel scroll, and
        // scroll-into-view; the window keeps only the page-action seams.
        _pageTabs = new PageTabsView(
            PanelPageTabs,
            ScrollerPageTabs,
            key => FindResource(key),
            SwitchToPage,
            RenamePage,
            DeletePage);

        // Profile persistence: load the saved profile at startup, falling back
        // to the starter profile when absent/corrupt. The provider lambda only
        // dereferences _profile at save time (import swaps the reference).
        _profilePersistence = new ProfilePersistence(
            ProfilePersistence.DefaultProfilePath(),
            () => _profile!,
            log: msg => FileLog.Write($"[PROFILE] {msg}"));

        // 4. Load the persisted profile, or build the starter profile on first
        //    launch. A first launch persists the starter immediately so the
        //    file exists before any mutation.
        var loaded = _profilePersistence.Load(_loader, this);
        if (loaded is null)
        {
            loaded = new StarterProfile(_loader, this).Create();
            _profilePersistence.Save();
        }
        _profile = loaded;
        _pageTabs.Rebuild(_profile);

        // 5. Route device touch input through the single input module. Display
        // touches are runtime input: Press/Move/Release cross the controller's
        // source-aware surface with the edit-mode flag off, so hotkeys fire on
        // the device even while the desktop is in edit mode — only the mouse
        // path carries the desktop edit-mode veto.
        _usbDevice.OnTouchEvent += (point, touchType) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (touchType == TouchEventType.TouchDown)
                {
                    _inputController.Press(point.X, point.Y, Input.InputSource.Device, editMode: false);
                }
                else if (touchType == TouchEventType.TouchMove)
                {
                    _inputController.Move(point.X, point.Y, Input.InputSource.Device, editMode: false, out _);
                }
                else
                {
                    _inputController.Release(point.X, point.Y, Input.InputSource.Device, editMode: false, out _);
                }
            });
        };

        // 6. Start 30 FPS Skia Render Loop & Hardware Frame Streamer. The pump
        // composes + sends once per tick, then repaints so the window draws
        // the same buffer it sent; the badge refresh rides the tick. The
        // compose gate skips the tick while the delivery is still writing the
        // previous frame (~55ms bulk write vs 33ms tick) — the display can't
        // take another frame anyway, so composing during the write is dead CPU.
        _framePump = new FramePump(
            composeAndSend: () =>
            {
                _compositor.Compose(_profile.ActivePage);
                _presenter.Send(_compositor.FrameBuffer);
            },
            requestRepaint: () => SkiaCanvas.InvalidateVisual(),
            onTick: UpdateUsbBadge,
            composeGate: () => !_presenter.IsSending);
        _framePump.Start(); // Start the render loop immediately

        // 7. Clean lifecycle shutdown on window close / debugging stop
        Closed += (s, e) =>
        {
            // The teardown sequence begins: OCEs raised by the disposes below
            // are expected and benign (see App.DispatcherUnhandledException).
            App.IsClosing = true;
            try
            {
                _framePump.Dispose();
                _telemetry.Dispose();
                _presenter.Dispose();
                ProfileOps.DisposeProfile(_profile);
                _dialogHost.CloseDeviceAuthorization();
                _compositor.Dispose();
            }
            finally
            {
                // The engine dispose is the one step that must never be
                // skipped: the display must reach standby on every exit, even
                // when an earlier teardown step throws.
                _usbDevice.Dispose();
            }
        };

        // Update USB badge
        UpdateUsbBadge();
        UpdateActiveCount();
        _inspector.Refresh();

        // The compositor defaults to runtime mode (no edit chrome); the Edit
        // Mode checkbox defaults to checked, and its Checked event fires
        // during InitializeComponent while the _wired guard is still off — so
        // re-assert the checkbox state onto the compositor here explicitly.
        _compositor.IsEditMode = ChkEditMode.IsChecked == true;

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

    /// <summary>
    /// One refresh sequence after a page-structure mutation (add/delete/rename/
    /// switch/import): rebuild the tab strip (scrolling to the active tab) and
    /// refresh the selection, count, and canvas. Passing the current selection
    /// keeps it (rename); null clears it (page add/delete/switch/import).
    /// </summary>
    private void RefreshAfterMutation(PlacedWidgetInstance? selection)
    {
        _pageTabs.Rebuild(_profile);
        SelectWidget(selection);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    /// <summary>
    /// One refresh sequence after a selection-only mutation (place/delete/
    /// clear): selects the given widget (null clears), refreshes the active
    /// count, and repaints the canvas.
    /// </summary>
    private void RefreshSelection(PlacedWidgetInstance? selection)
    {
        SelectWidget(selection);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    #region Skia Canvas Rendering & Mouse Interaction

    private void SkiaCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        // Pure draw: the FramePump composed this buffer and queued it for
        // delivery on this tick, so what is drawn is exactly what was sent.
        e.Surface.Canvas.DrawBitmap(_compositor.FrameBuffer, 0, 0, FrameSamplingOptions);
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The click-outside-deselect geometry rule is pure
        // (MainWindowInputPolicy) — the handler only feeds it the two
        // element-relative positions.
        if (!MainWindowInputPolicy.ShouldDeselect(
                e.GetPosition(SkiaCanvas), new Size(SkiaCanvas.ActualWidth, SkiaCanvas.ActualHeight),
                e.GetPosition(InspectorPanel), new Size(InspectorPanel.ActualWidth, InspectorPanel.ActualHeight)))
            return;

        SelectWidget(null);
    }

    private void SkiaCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        // Capture the mouse so a drag that leaves the canvas still delivers
        // Move/Up to the gesture machine and edit-mode manipulation.
        SkiaCanvas.CaptureMouse();
        var pos = e.GetPosition(SkiaCanvas);

        // The controller owns the press policy: hit-test → select → begin a
        // manipulation or feed the shared gesture machine (page navigation +
        // widget touch routing). The mouse carries the edit-mode veto: authoring
        // presses manipulate.
        _inputController.Press((float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit, _compositor.IsEditMode);
    }

    private void SkiaCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown) return;
        var pos = e.GetPosition(SkiaCanvas);

        // A manipulation consumes the sample; otherwise the controller feeds
        // the machine (page navigation + widget touch routing).
        if (_inputController.Move((float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit, _compositor.IsEditMode, out bool changed) && changed)
        {
            _inspector.RefreshTransforms();
            SkiaCanvas.InvalidateVisual();
        }
    }

    private void SkiaCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SkiaCanvas);

        // A manipulation gesture never reaches the gesture machine — it stays
        // wholly in the input controller (resize / drag / icon-drag). A plain
        // release feeds the machine's TouchUp.
        bool wasManipulating = _inputController.Release(
            (float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit, _compositor.IsEditMode, out bool iconMoved);

        _isMouseDown = false;

        if (wasManipulating)
        {
            _inspector.RefreshTransforms();
            SkiaCanvas.InvalidateVisual();
        }

        if (iconMoved)
            _inspector.Refresh();

        SkiaCanvas.ReleaseMouseCapture();
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
            RefreshSelection(null);
        }
    }

    /// <summary>
    /// Delete/Back removes the selected widget — except while typing in an
    /// inspector text box, where Backspace must edit the field, not delete.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Delete || e.Key == Key.Back) &&
            MainWindowInputPolicy.ShouldHandleDeleteKey(Keyboard.FocusedElement is TextBox))
        {
            DeleteSelectedWidget();
            e.Handled = true;
        }
    }

    #endregion

    #region Catalog, Header, and Action Handlers

    private void TxtSearchCatalog_TextChanged(object sender, TextChangedEventArgs e) => RefreshCatalog();

    /// <summary>One catalog sort, three call sites: the initial fill, the
    /// filter box, and the empty-query reset all render through this.</summary>
    private void RefreshCatalog()
    {
        ListCatalog.ItemsSource = CatalogFilter.Apply(_loader.RegisteredPlugins, TxtSearchCatalog.Text.Trim());
    }

    private void BtnPlaceWidget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pluginId)
        {
            var placed = ProfileOps.PlaceCentered(_profile, _loader, this, pluginId);
            if (placed == null) return;

            RefreshSelection(placed);
        }
    }

    private void RenamePage(int index)
    {
        if (index < 0 || index >= _profile.Pages.Count) return;
        var page = _profile.Pages[index];

        string? newName = _dialogHost.PromptForText($"Rename Page", $"New name for '{page.PageName}':", page.PageName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        ProfileOps.RenamePage(page, newName);
        RefreshAfterMutation(_selectedWidget);
    }

    private void SwitchToPage(int index)
    {
        if (!ProfileOps.SetActivePageIndex(_profile, index)) return;
        RefreshAfterMutation(null);
    }

    private void DeletePage(int index)
    {
        if (!PageTabsViewModel.CanDelete(_profile)) return;
        var targetPage = _profile.Pages[index];
        if (targetPage.Widgets.Count > 0 && !_dialogHost.Confirm("Delete Page", $"Are you sure you want to delete '{targetPage.PageName}' containing {targetPage.Widgets.Count} widget(s)?"))
            return;

        if (!ProfileOps.DeletePage(_profile, index)) return;
        RefreshAfterMutation(null);
    }

    private void BtnAddPage_Click(object sender, RoutedEventArgs e)
    {
        ProfileOps.AddPage(_profile);
        RefreshAfterMutation(null);
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
            try
            {
                string json = ProfileOps.ExportJson(_profile);
                File.WriteAllText(dlg.FileName, json);
                _dialogHost.Info("Export Complete", "Profile exported successfully!");
            }
            catch (Exception ex)
            {
                _dialogHost.Error("Export Error", $"Error exporting profile: {ex.Message}");
            }
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Display Profile (*.json)|*.json" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                // Untrusted input: cap the file read before any parsing — the
                // same reject-oversized-input spirit as the import sanitizer
                // caps in ProfileOps.
                if (ProfileOps.IsImportFileTooLarge(new FileInfo(dlg.FileName).Length))
                {
                    _dialogHost.Error("Import Error", "The selected profile file is too large to import.");
                    return;
                }

                string json = File.ReadAllText(dlg.FileName);
                var loaded = ProfileOps.ImportJson(json, _loader, this);
                if (loaded != null)
                {
                    // One swap site: ReplaceProfile disposes the old profile's
                    // widget instances and returns the imported profile active.
                    _profile = ProfileOps.ReplaceProfile(_profile, loaded);

                    // Resync the toggle: the imported page's snap-to-grid may
                    // differ from the checkbox's current state. Applied
                    // directly from the import result — no reliance on the
                    // change-handler write-back loop.
                    ChkSnapToGrid.IsChecked = _profile.ActivePage.SnapToGrid;
                    RefreshAfterMutation(null);
                }
            }
            catch (Exception ex)
            {
                _dialogHost.Error("Import Error", $"Error importing profile: {ex.Message}");
            }
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogHost.Confirm("Confirm Clear", "Are you sure you want to clear all widgets from the current page?"))
        {
            ProfileOps.ClearPage(_profile.ActivePage);
            RefreshSelection(null);
        }
    }

    #endregion


    private readonly LogOnChange _usbBadgeChanged = new();

    private void UpdateUsbBadge()
    {
        var (label, brushKey) = UsbBadgeModel.From(_usbDevice.State);
        if (!_usbBadgeChanged.Changed(brushKey + label)) return; // state unchanged — skip the per-tick resource lookup

        var resources = Application.Current.Resources;
        UsbStatusDot.Fill = (Brush)resources[brushKey];
        TxtUsbStatus.Text = label;
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ShowThemeDialog();
    }

    private void ShowThemeDialog()
    {
        new Dialogs.ThemeDialog(this, _themeApplicator).ShowDialog();
    }

    private static void Log(string msg) => FileLog.Write(msg);
}
