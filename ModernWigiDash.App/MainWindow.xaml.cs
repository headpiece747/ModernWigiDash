using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernWigiDash.App.Power;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Hardware.Transport;

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
    private FramePump _framePump = null!;

    /// <summary>Windows sleep/resume lifecycle: pauses the pump on suspend and
    /// restarts it (plus a forced USB reconnect) on resume.</summary>
    private Power.PowerLifecycle _powerLifecycle = null!;

    /// <summary>Owns the telemetry producers (sensor + frame-time poll loops).
    /// The PresentMon interop is injected as a ctor parameter so the window
    /// can be constructed with a fake in tests (no real DLL load).</summary>
    private TelemetryProducers _telemetry = null!;

    // The wiring-assigned fields (_framePump, _powerLifecycle, _telemetry,
    // _profile, _inputController, _deviceTouchDrain, _delivery, _inspector,
    // _dialogHost, _pageTabs, _profilePersistence, _tray) are null!-typed: they
    // hold null in the startup artifact's pre-module window, which the
    // context's null-tolerant facade (MainWindow.Context.cs) treats as a
    // benign no-op. BuildStartupWiring assigns them in its step order; the
    // ordering facts are pinned by StartupWiringTests.

    private ProfileLayout _profile = null!;
    private PlacedWidgetInstance? _selectedWidget;

    /// <summary>Test seam: the live active page index — the device-touch
    /// drain's navigation outcome is observable without a UI assertion.</summary>
    internal int ActivePageIndex => _profile?.ActivePageIndex ?? 0;

    // XAML-fired events can arrive during InitializeComponent, before the ctor
    // assigns the modules they forward to (e.g. the opacity slider's initial
    // ValueChanged). Guarded handlers no-op until this is set, as the last
    // ctor statement.
    private bool _wired;

    /// <summary>The explicit-quit flag (ADR-0018): set by the tray's Quit
    /// (<see cref="QuitClose"/>) before the close, so the close intercept -
    /// which hides to the tray when the close behavior is on - vetoes itself
    /// and the tray's Quit always exits. The tray's Quit is the only exit
    /// when the behavior is on.</summary>
    private bool _quitting;

#pragma warning disable S125 // input-handling documentation, not commented-out code
    // Mouse & Swipe Gesture Interaction State. The gesture machine, its outcome
    // application, and edit-mode manipulation decisions live in InputController;
    // MainWindow only tracks button state and drives UI refresh.
#pragma warning restore S125
    private bool _isMouseDown = false;
    private Input.InputController _inputController = null!;

    // Device touch events arrive on the engine's 16 ms poll thread and must
    // reach the gesture machine on the UI thread IN ORDER (the Down/Move/Up
    // sequence is one gesture). The per-event work is a lock + a struct
    // enqueue (Queue<T> over a value tuple never boxes); one drain callback
    // per burst feeds the input module instead of one closure +
    // DispatcherOperation per event.
    private readonly Queue<(float X, float Y, TouchEventType Type)> _deviceTouchQueue = new();
    private readonly Lock _deviceTouchLock = new();
    private bool _deviceTouchDrainScheduled;
    private Action _deviceTouchDrain = null!;

    // Frame presentation — decouple the UI render timer from transport
    // round-trips: one FrameDelivery bound to the direct-USB engine (ADR-0005)
    // with the single encode→pool→coalesce→pace policy and the 33ms pacing the
    // engine's USB writes used; the production encoder (SkiaRgb565Encoder)
    // binds at this one Create site.
    private FrameDelivery _delivery = null!;

    // The App's file-log vocabulary: one bound DiagLog per log area — the tag
    // binds once at construction (the DiagLog module) instead of being
    // concatenated into every line, so the vocabulary cannot drift.
    private readonly DiagLog _hwLog = new("HW", 1);
    private readonly DiagLog _profileLog = new("PROFILE", 1);
    private readonly DiagLog _trayLog = new("TRAY", 1);

    // Deep modules: the property inspector, the small host dialogs, the page
    // tabs strip, and the default profile builder own their logic; the window
    // keeps wiring (the startup artifact's HostModules step).
    private Inspector.InspectorController _inspector = null!;
    private DialogHost _dialogHost = null!;
    private PageTabsView _pageTabs = null!;

    /// <summary>The notification-area icon (ADR-0018): the show/quit routing
    /// plus the <see cref="TrayIconController.IsLive"/> guard the close path
    /// reads (a dead tray falls the close through to a normal exit). Wired by
    /// the startup Tray step, removed by the plan's TrayDispose step.</summary>
    private TrayIconController _tray = null!;

    /// <summary>The tray surface the Tray wiring step hands to the
    /// controller: null in production (the controller creates the WinForms
    /// adapter), an injected fake in the test host (whose own Application
    /// must survive, and whose test output dir has no icon resource, so the
    /// production surface would read dead and the N1 fallback would swallow
    /// every hide).</summary>
    private readonly ITrayIconSurface? _traySurface;

    /// <summary>The session-end standby seam: null in production (routes to
    /// the engine's real <c>TryGoToStandby</c>), an injected probe in the
    /// test host (whose engine is the real engine - a unit test must never
    /// drive a standby ritual at the user's attached display).</summary>
    private readonly Func<bool>? _sessionEndStandby;

    // Profile persistence: loads the saved profile at startup and owns the
    // debounced save of the current profile (wired by the startup artifact
    // before the profile load step).
    private ProfilePersistence _profilePersistence = null!;

    // Theme application: resources + preview shadow + per-window DWM chrome +
    // the applied-log line, all behind one seam (ThemeApplicator).
    private readonly ThemeApplicator _themeApplicator = new();

    /// <summary>Sampling options for the preview draw — hoisted so the per-frame
    /// paint never allocates a new instance.</summary>
    private static readonly SKSamplingOptions FrameSamplingOptions = new(SKFilterMode.Linear);

    /// <summary>Production constructor: the real PresentMon interop, the
    /// default profile path, and the live system power-mode source.</summary>
    public MainWindow()
        : this(new PresentMonNative(), ProfilePersistence.DefaultProfilePath(), new SystemPowerModeSource())
    {
    }

    /// <summary>Test seam: the native PresentMon interop is injected so window
    /// construction never loads the real DLL in the test host. Power events
    /// stay inert (a test host must never subscribe to real SystemEvents).</summary>
    internal MainWindow(IPresentMonNative presentMonNative)
        : this(presentMonNative, ProfilePersistence.DefaultProfilePath(), new NoopPowerModeSource())
    {
    }

    /// <summary>Test seam: injects the native PresentMon interop AND the
    /// persisted-profile path so window-level tests never read/write the real
    /// LocalAppData profile file. Power events stay inert.</summary>
    internal MainWindow(IPresentMonNative presentMonNative, string profilePath)
        : this(presentMonNative, profilePath, new NoopPowerModeSource())
    {
    }

    /// <summary>Full seam: the power-mode source is injectable so production
    /// subscribes to SystemEvents and test hosts pass a no-op; the tray
    /// surface is injectable so the window's close-intercept tests drive a
    /// live fake; the session-end standby probe is injectable so the
    /// routing pin never drives a standby ritual at the user's attached
    /// display.</summary>
    internal MainWindow(IPresentMonNative presentMonNative, string profilePath, IPowerModeSource powerModeSource, ITrayIconSurface? traySurface = null, Func<bool>? sessionEndStandby = null)
    {
        _traySurface = traySurface;
        _sessionEndStandby = sessionEndStandby;
        // The engine is inert until Start: construction never probes USB, the
        // window's field initializer only allocates. Start the background
        // connect + touch poll explicitly. Start precedes InitializeComponent:
        // the engine's events hop to the dispatcher (Hop), and the initial
        // connect is in flight before the XAML tree exists, so the first paint
        // reflects the real connection state.
        _usbDevice.Start();
        InitializeComponent();
        SourceInitialized += (_, _) => _themeApplicator.Apply(this);
        SourceInitialized += OnUpdateCheckAtStartup;
        PreviewMouseDown += OnWindowPreviewMouseDown;

        // The startup wiring is one named artifact (BuildStartupWiring),
        // applied in order — the TeardownPlan image for startup. The sequence
        // is the load-bearing knowledge (the host modules before the profile
        // load, the resyncs before the wired arm, the wired arm last), pinned
        // by StartupWiringTests; a reorder fails a pin instead of sailing
        // into the historical startup NRE (and the context's null-tolerant
        // module derefs keep even a missed reorder a benign no-op, not a
        // crash).
        foreach (WiringStep step in BuildStartupWiring(presentMonNative, profilePath, powerModeSource).OrderedSteps)
        {
            step.Run();
        }
    }

    /// <summary>
    /// The window's startup wiring as one named artifact: the ordered
    /// construction steps. The sequence is the load-bearing knowledge (the
    /// host modules before the profile load — a widget's InitializeAsync runs
    /// synchronously inside the load and calls back into the context; the
    /// state resyncs before the wired arm so their XAML events stay guarded;
    /// the wired arm last so the guarded handlers arm only after every module
    /// exists) — pinned against this real list by <c>StartupWiringTests</c>,
    /// the way <c>TeardownPlanTests</c> pins <see cref="BuildTeardownPlan"/>.
    /// </summary>
    internal StartupWiring BuildStartupWiring(IPresentMonNative presentMonNative, string profilePath, IPowerModeSource powerModeSource) => new(
    [
        new WiringStep("FrameDelivery", () => _delivery = FrameDelivery.Create(
            encoder: new SkiaRgb565Encoder(),
            send: _usbDevice.SendFrameBytes,
            isReady: () => _usbDevice.CanSendFrames,
            log: _hwLog.Write)),

        new WiringStep("Telemetry", () =>
        {
            // One poll loop per direct producer, owned by the telemetry
            // module: SENSOR (LHS shared memory, ADR-0004) and FRAMETIME
            // (PresentMon, ADR-0003) start immediately and stop on close.
            _telemetry = new TelemetryProducers(presentMonNative, Log);
            _telemetry.Start();

            // The engine's Start() above fires the initial connect. Do NOT
            // block the UI thread waiting for USB — the render timer will
            // start sending frames as soon as the connection succeeds.
        }),

        new WiringStep("WidgetCatalog", () =>
        {
            // Attribute-driven catalog: adding a widget to the Widgets
            // assembly needs no registration here.
            _loader.RegisterBuiltInAssembly(typeof(DigitalAnalogClockWidget).Assembly);
            // Populate the catalog UI (sorted alphabetically by display name).
            RefreshCatalog();
        }),

        new WiringStep("ProfilePersistence", () =>
        {
            // Profile persistence: owns the LocalAppData path and the
            // debounced save. Wired before the host modules so the
            // inspector's onProfileChanged hook can reference it; the
            // provider lambda only dereferences _profile at save time
            // (import swaps the reference).
            _profilePersistence = new ProfilePersistence(
                profilePath,
                () => _profile,
                log: _profileLog.Write);
        }),

        new WiringStep("HostModules", () =>
        {
            // Build the host modules (input, inspector, dialog host) BEFORE
            // the profile load. Widget InitializeAsync runs synchronously
            // inside the load and may call back into the context (e.g.
            // Twitch's RestoreTwitchSessionAsync -> RequestInspectorRefresh /
            // ShowDeviceAuthorization). This step's position removes the
            // startup NRE when those callbacks arrive before the modules
            // exist; the context's null-tolerant module derefs are the
            // backstop.
            //
            // Single input module: gesture machine + outcome application +
            // edit-mode manipulation + the press orchestration. All input
            // sources cross its source-aware surface; page-switch UI work
            // stays here.
            _inputController = new Input.InputController(
                // _profile is assigned by the ProfileLoad step below, before
                // any input event can run — the lambda only dereferences it
                // at invoke time.
                () => new Input.InputState(_profile.ActivePage, _profile.Pages.Count, _profile.ActivePageIndex),
                // The desktop's live edit-mode read: the controller derives
                // the manipulation/routing veto from the source, so the call
                // sites pass coordinates and the source only.
                () => _compositor.IsEditMode,
                navigateTo: SwitchToPage,
                requestRender: () => SkiaCanvas.InvalidateVisual(),
                select: SelectWidget,
                onManipulation: HandleManipulationChange);

            // The drain callback is a cached delegate (a method group would be
            // converted on every enqueue; the field holds one instance).
            _deviceTouchDrain = DrainDeviceTouchQueue;

            // One stateful DialogHost for the whole window: the inspector
            // receives this instance (it must never build its own — a second
            // instance could never show the device-authorization window it
            // owns).
            _dialogHost = new DialogHost(this, _themeApplicator, TryFindResource, LogError);

            _inspector = new Inspector.InspectorController(
                new Inspector.TransformFieldBindings(
                    PosX: TxtPosX,
                    PosY: TxtPosY,
                    WidthText: TxtWidth,
                    HeightText: TxtHeight,
                    ZIndexText: TxtZIndex,
                    RotationText: TxtRotation,
                    OpacitySlider: SliderOpacity,
                    OpacityValueText: TxtOpacityVal,
                    RequestCanvasRender: () => SkiaCanvas.InvalidateVisual()),
                new Inspector.CustomPropertyPanel(
                    emptyPanel: PanelEmptyInspector,
                    activePanel: PanelActiveInspector,
                    nameText: TxtInspName,
                    customProperties: PanelCustomProperties,
                    tryFindResource: TryFindResource),
                () => _selectedWidget,
                _dialogHost,
                this,
                onProfileChanged: () => _profilePersistence.MarkDirty(),
                commitLocationPick: candidate =>
                {
                    if (_selectedWidget?.ActiveInstance is IWidgetLocationSearch search)
                    {
                        search.CommitPick(candidate);
                    }
                });

            // Page-tabs strip module: owns tab construction, the wheel scroll,
            // and scroll-into-view; the window keeps only the page-action
            // seams.
            _pageTabs = new PageTabsView(
                PanelPageTabs,
                ScrollerPageTabs,
                key => FindResource(key),
                SwitchToPage,
                RenamePage,
                DeletePage);

            // Page-background picker: the swatch commits the active page's
            // color. Its Hex is kept in sync by ApplyProfileMutation (the
            // mutation funnel).
            PageBgPicker.Applied += OnPageBackgroundApplied;
        }),

        new WiringStep("ProfileLoad", () =>
        {
            // Load the persisted profile, or build the starter profile on
            // first launch. A first launch persists the starter immediately
            // so the file exists before any mutation. Runs AFTER HostModules:
            // the rehydrated/starter widgets' InitializeAsync runs
            // synchronously here and may call back into the context — the
            // modules must exist (the ordering fact this step's position
            // pins).
            var loaded = _profilePersistence.Load(_loader, this);
            _profile = loaded ?? new StarterProfile(_loader, this).Create();
            if (loaded is null)
            {
                _profilePersistence.Save();
            }
            _pageTabs.Rebuild(_profile);
            PageBgPicker.Hex = _profile.ActivePage.BackgroundHexColor;
        }),

        new WiringStep("SnapToGridResync", () =>
        {
            // Resync the page-level toggle from the loaded profile the same
            // way the mutation funnel does after an import (the XAML default
            // is true; a persisted page may differ). _wired is still off, so
            // the checkbox event this resync fires is guarded: a startup
            // state resync is not a mutation and must not arm a save.
            ChkSnapToGrid.IsChecked = _profile.ActivePage.SnapToGrid;
        }),

        new WiringStep("DeviceTouchRoute", () =>
        {
            // Route device touch input through the single input module.
            // Display touches are runtime input: Press/Move/Release cross the
            // controller's source-aware surface, so hotkeys fire on the device
            // even while the desktop is in edit mode — only the mouse path
            // carries the desktop edit-mode veto.
            _usbDevice.OnTouchEvent += EnqueueDeviceTouch;
        }),

        new WiringStep("FramePump", () =>
        {
            // Start 30 FPS Skia Render Loop & Hardware Frame Streamer. The
            // pump composes + sends once per tick, then repaints so the
            // window draws the same buffer it sent; the badge refresh rides
            // the tick. The compose gate skips the tick while the delivery is
            // still writing the previous frame (~55ms bulk write vs 33ms
            // tick) — the display can't take another frame anyway, so
            // composing during the write is dead CPU. AFTER ProfileLoad: the
            // compose reads _profile; AFTER FrameDelivery: it pushes into
            // _delivery.
            _framePump = new FramePump(
                composeAndSend: () =>
                {
                    _compositor.Compose(_profile.ActivePage);
                    _delivery.Push(_compositor.FrameBuffer);
                },
                requestRepaint: () => SkiaCanvas.InvalidateVisual(),
                onTick: UpdateUsbBadge,
                composeGate: () => !_delivery.IsSendInFlight);
            _framePump.Start();
        }),

        new WiringStep("PowerLifecycle", () =>
        {
            // Power lifecycle: SystemEvents fires on a system thread, so both
            // actions hop to the dispatcher via the single Hop helper.
            // Suspend stops the pump (no dead compose ticks while the display
            // is powered down); resume restarts it and forces the USB engine
            // to reconnect — Start() is guarded, so the extra call is
            // harmless when the transport never dropped.
            _powerLifecycle = new Power.PowerLifecycle(
                powerModeSource,
                onSuspend: () => Hop(() => _framePump.Stop()),
                onResume: () => Hop(() =>
                {
                    _framePump.Start();
                    _usbDevice.Start();
                }));
        }),

        new WiringStep("TeardownHook", () =>
        {
            // Clean lifecycle shutdown on window close / debugging stop. The
            // plan is a named artifact (BuildTeardownPlan) the orchestrator
            // runs — the sequence is assertable against the real list.
            Closed += (s, e) =>
            {
                // The teardown sequence begins: a throwing step is isolated
                // (one [TEARDOWN] line, the plan continues), so a long-lived
                // host never inherits the modules the aborted tail would
                // have disposed, and the display-standby last resort runs
                // no matter what.
                App.IsClosing = true;
                new ShutdownOrchestrator(BuildTeardownPlan(), Log).Run();
            };
        }),

        new WiringStep("InitialRefresh", () =>
        {
            UpdateUsbBadge();
            UpdateActiveCount();
            // The final inspector refresh re-establishes the panel after the
            // profile load — and it is the repair that makes a pre-module
            // RequestInspectorRefresh a benign no-op (the context's
            // null-tolerant facade): whatever a callback lost before the
            // modules existed, this step re-requests.
            _inspector.Refresh();
        }),

        new WiringStep("EditModeResync", () =>
        {
            // The compositor defaults to runtime mode (no edit chrome); the
            // Edit Mode checkbox defaults to checked, and its Checked event
            // fires during InitializeComponent while the _wired guard is still
            // off — so re-assert the checkbox state onto the compositor here
            // explicitly, before the wired arm below.
            _compositor.IsEditMode = ChkEditMode.IsChecked == true;
        }),

        new WiringStep("Tray", () =>
        {
            // The notification-area icon (ADR-0018): always present, the
            // single-click and the menu's show item route to ShowFromTray,
            // the menu's Quit closes through the normal close sequence
            // (teardown + standby) and then shuts the app down explicitly -
            // a hidden window's Close does not trip OnLastWindowClose.
            // BEFORE the wired arm: its handlers forward to the window like
            // every other module.
            _tray = new TrayIconController(
                onShow: ShowFromTray,
                onQuit: QuitFromTray,
                log: _trayLog,
                surface: _traySurface);
            _tray.Start();
        }),

        new WiringStep("CloseIntercept", () =>
        {
            // The close behavior (ADR-0018): X, Alt+F4, and minimize hide to
            // the tray instead of closing or minimizing when the profile's
            // close behavior is the tray keep-alive AND the tray icon is
            // live (N1: a dead tray falls the action through to the normal
            // behavior, because a hidden window with no tray is
            // unreachable). The decision routes through
            // CloseInterceptPolicy so a hand-edited profile can never
            // smuggle in a hide. SessionEnding runs the non-disposing
            // standby seam: a system shutdown while the display is live is
            // the documented wedge case (the display has no auto-sleep),
            // and the session end is the one chance to run the ritual
            // before the process dies.
            Closing += OnWindowClosing;
            StateChanged += OnWindowStateChanged;
            Application.Current.SessionEnding += (_, _) => RunSessionEndStandby();
        }),

        new WiringStep("Wired", () =>
        {
            // The wired arm is the LAST step: the guarded XAML handlers (the
            // edit-mode checkbox, the snap toggle, the transform boxes, the
            // opacity slider) arm only after every module the handlers
            // forward to exists.
            _wired = true;
        })
    ]);

    /// <summary>
    /// The window's teardown plan as one named artifact: the ordered steps +
    /// the never-skip last resort. The sequence is the load-bearing knowledge
    /// (persist before teardown, the pump disposes before the delivery it
    /// pushes into, the engine strictly last) — pinned against this real list
    /// by <c>TeardownPlanTests</c>; the orchestrator's synthetic steps pin the
    /// run policy, not the sequence.
    /// </summary>
    internal TeardownPlan BuildTeardownPlan() => new(
    [
        // Persist before teardown: a clean exit always lands the final
        // profile state (including the last active page index).
        new TeardownStep("ProfilePersist", _profilePersistence.Flush),
        new TeardownStep("ProfilePersistence", _profilePersistence.Dispose),
        // The pump stops before the delivery it pushes into: a compose tick
        // must never land on a disposed delivery.
        new TeardownStep("FramePump", _framePump.Dispose),
        new TeardownStep("PowerLifecycle", _powerLifecycle.Dispose),
        new TeardownStep("Telemetry", _telemetry.Dispose),
        new TeardownStep("FrameDelivery", _delivery.Dispose),
        new TeardownStep("Profile", () => ProfileOps.DisposeProfile(_profile)),
        new TeardownStep("DeviceAuthorization", _dialogHost.CloseDeviceAuthorization),
        // The tray icon is removed after the profile state has landed (the
        // persist-first guarantee covers the exit affordance: the user who
        // saw the icon go can trust the profile was saved).
        new TeardownStep("TrayDispose", _tray.Dispose),
        new TeardownStep("Compositor", _compositor.Dispose)
    ],
    // The engine dispose is the one step that must never be skipped:
    // the display must reach standby on every exit, even when an
    // earlier teardown step throws (the orchestrator's last resort).
    new TeardownStep("UsbEngineStandby", _usbDevice.Dispose));

    /// <summary>
    /// The device-touch hop to the UI thread (engine 16 ms poll thread →
    /// dispatcher): the struct is enqueued under the lock, and a drain callback
    /// is scheduled only when none is pending — so a burst of N touch events
    /// allocates one DispatcherOperation instead of N closures + N operations.
    /// Internal so the test host can enqueue the engine's event shape and pin
    /// the queue's ordering contract without hardware.
    /// </summary>
    internal void EnqueueDeviceTouch(SKPoint point, TouchEventType touchType)
    {
        bool schedule;
        lock (_deviceTouchLock)
        {
            _deviceTouchQueue.Enqueue((point.X, point.Y, touchType));
            schedule = !_deviceTouchDrainScheduled;
            _deviceTouchDrainScheduled = true;
        }
        if (schedule)
        {
            try
            {
                _ = Dispatcher.BeginInvoke(_deviceTouchDrain);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
            {
                // A shutting-down dispatcher rejects the post: unpoison the
                // flag so the next event schedules a fresh drain (a redundant
                // second drain is harmless — it runs to empty).
                lock (_deviceTouchLock)
                {
                    _deviceTouchDrainScheduled = false;
                }
            }
        }
    }

    /// <summary>
    /// Drains the queued device-touch events on the UI thread, in order, into
    /// the input controller (the gesture machine's Down/Move/Up vocabulary).
    /// The whole burst is dequeued under the queue lock, then fed to the
    /// controller OUTSIDE it: the engine's 16 ms poll thread (the enqueue
    /// side) contends on the queue only, never on the input pass itself.
    /// Ordering survives the split: the batch is fed in dequeue order, and
    /// the dispatcher serializes drains (one at a time via the scheduled
    /// flag), so a later burst's events can never land ahead of an earlier
    /// one's. Internal so the test host can drive one deterministic drain on
    /// the UI thread and pin the in-order feed against the gesture machine.
    /// </summary>
    internal void DrainDeviceTouchQueue()
    {
        // Snapshot the burst under the lock, release, then feed: an input
        // pass that held the lock would make the poll thread's next Enqueue
        // wait out the whole gesture feed (the old deliberate trade, retired).
        List<(float, float, TouchEventType)> batch;
        lock (_deviceTouchLock)
        {
            _deviceTouchDrainScheduled = false;
            batch = new List<(float, float, TouchEventType)>(_deviceTouchQueue.Count);
            while (_deviceTouchQueue.Count > 0)
                batch.Add(_deviceTouchQueue.Dequeue());
        }

        foreach (var (x, y, type) in batch)
        {
            if (type == TouchEventType.TouchDown)
            {
                // Device samples are runtime input: the controller derives
                // "never suppressed" from the source itself.
                _inputController.Press(x, y, Input.InputSource.Device);
            }
            else if (type == TouchEventType.TouchMove)
            {
                _inputController.Move(x, y, Input.InputSource.Device, out _);
            }
            else
            {
                _inputController.Release(x, y, Input.InputSource.Device, out _);
            }
        }
    }

    private void SelectWidget(PlacedWidgetInstance? widget)
    {
        // The same-reference early-out keeps the mutation contract's
        // selection re-application free when nothing changed (the in-page
        // shapes pass the current selection straight through), and protects
        // the re-entrant import path: the funnel's control resyncs can fire a
        // handler that re-enters the contract while the old — now disposed —
        // selected instance is still referenced, and re-applying it must not
        // rebuild the inspector over a dead widget.
        if (ReferenceEquals(widget, _selectedWidget)) return;
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
    /// The ONE post-mutation contract: whatever shape a mutation takes, its
    /// post-conditions run exactly once here, so a call site never re-derives
    /// "what happens after a mutation" (refresh shape, dirty mark, structural
    /// flag) per site — doubled or missing marks are unrepresentable. The
    /// shape selects the refresh bundle:
    /// <see cref="ProfileMutationShape.Structural"/> re-syncs the tab strip and
    /// the page-background picker (the page set changed);
    /// <see cref="ProfileMutationShape.RawWrite"/> (an import) additionally
    /// re-syncs the snap-to-grid toggle from the imported page, whose handler
    /// routes through this same contract;
    /// <see cref="ProfileMutationShape.Transform"/> re-syncs nothing structural
    /// (in-page state only). Every shape then re-applies the selection (the
    /// caller always passes the post-mutation selection; in-page shapes pass the
    /// unchanged one), refreshes the active count, repaints the canvas, and
    /// marks the profile dirty exactly once. Inspector-driven write-backs
    /// (transform text, opacity, property values) are the one path that marks
    /// through the inspector's onProfileChanged callback instead — exactly one
    /// invocation per landed write-back, and the window's forwarding handlers
    /// add none.
    /// </summary>
    internal void ApplyProfileMutation(ProfileMutationShape shape, PlacedWidgetInstance? selection)
    {
        if (shape is ProfileMutationShape.Structural or ProfileMutationShape.RawWrite)
        {
            _pageTabs.Rebuild(_profile);
            PageBgPicker.Hex = _profile.ActivePage.BackgroundHexColor;
        }

        if (shape is ProfileMutationShape.RawWrite)
        {
            // A raw write replaces the whole profile state, so the imported page's
            // snap-to-grid may differ from the checkbox's old page's: the resync
            // routes through the checkbox's own handler, which re-derives the
            // profile value from the control and thus keeps one source of truth
            // (no bypass of the write-back loop). On import the handler is wired
            // and idempotently re-enters this same contract with the unchanged
            // value; on the startup resync it is still guarded off by _wired.
            ChkSnapToGrid.IsChecked = _profile.ActivePage.SnapToGrid;
        }

        SelectWidget(selection);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
        _profilePersistence.MarkDirty();
    }

    /// <summary>Page-background picker commit: writes the active page's
    /// BackgroundHexColor (the compositor diffs it per frame, so the change
    /// flows to the physical display on the next tick). The post-conditions ride
    /// the mutation funnel; the swatch itself is kept in sync by the commit.</summary>
    private void OnPageBackgroundApplied(string hex)
    {
        _profile.ActivePage.BackgroundHexColor = hex;
        ApplyProfileMutation(ProfileMutationShape.Transform, _selectedWidget);
    }

    #region Skia Canvas Rendering & Mouse Interaction

    private void SkiaCanvas_PaintSurface(object _, SKPaintSurfaceEventArgs e)
    {
        // Pure draw: the FramePump composed this buffer and queued it for
        // delivery on this tick, so what is drawn is exactly what was sent.
        e.Surface.Canvas.DrawBitmap(_compositor.FrameBuffer, 0, 0, FrameSamplingOptions);
    }

    private void OnWindowPreviewMouseDown(object _, MouseButtonEventArgs e)
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

    private void SkiaCanvas_MouseDown(object _, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        // Capture the mouse so a drag that leaves the canvas still delivers
        // Move/Up to the gesture machine and edit-mode manipulation.
        SkiaCanvas.CaptureMouse();
        var pos = e.GetPosition(SkiaCanvas);

        // The controller owns the press policy: hit-test → select → begin a
        // manipulation or feed the shared gesture machine (page navigation +
        // widget touch routing). The edit-mode veto is derived from the
        // source inside the controller (it reads the compositor's live edit
        // mode itself), so the handler passes coordinates and the source only.
        _inputController.Press((float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit);
    }

    private void SkiaCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown) return;
        var pos = e.GetPosition(SkiaCanvas);

        // A manipulation consumes the sample; otherwise the controller feeds
        // the machine (page navigation + widget touch routing). The refresh
        // after a manipulation is the controller's onManipulation funnel.
        _inputController.Move((float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit, out _);
    }

    private void SkiaCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SkiaCanvas);

        // A manipulation gesture never reaches the gesture machine — it stays
        // wholly in the input controller (resize / drag / icon-drag). A plain
        // release feeds the machine's TouchUp. The release funnel persists and
        // refreshes when a manipulation ended (including snap-to-grid).
        _inputController.Release(
            (float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit, out _);

        _isMouseDown = false;
        SkiaCanvas.ReleaseMouseCapture();
    }

    /// <summary>
    /// One dispatcher-hop convention for callbacks raised on non-UI threads
    /// (SystemEvents, the USB engine): hop to the dispatcher and, unless the
    /// source is exempted, skip work after the close/teardown sequence began.
    /// </summary>
    private void Hop(Action action, bool guardClose = true)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (guardClose && App.IsClosing) return;
            action();
        });
    }

    /// <summary>
    /// One refresh funnel for every manipulation outcome: the input controller
    /// reports what changed; the window applies the same post-condition at
    /// every call site (move + release), so the device-touch path (which never
    /// manipulates) needs no special case.
    /// </summary>
    private void HandleManipulationChange(Input.ManipulationChange change)
    {
        if (change.Changed)
        {
            _profilePersistence.MarkDirty();
            _inspector.RefreshTransforms();
            SkiaCanvas.InvalidateVisual();
        }

        if (change.IconMoved)
        {
            _inspector.Refresh();
        }
    }

    #endregion

    #region Inspector event forwarding (logic lives in Inspector.InspectorController)

    /// <summary>
    /// One forward only: the inspector's write-back seam fires onProfileChanged
    /// (exactly once per landed write-back), and that callback IS the dirty
    /// mark on the inspector-driven path — a mark here would double it.
    /// </summary>
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

    private void BtnDeleteWidget_Click(object _, RoutedEventArgs e)
    {
        DeleteSelectedWidget();
    }

    /// <summary>Single delete path shared by the inspector button and the Delete/Back key.</summary>
    private void DeleteSelectedWidget()
    {
        if (_selectedWidget != null)
        {
            ProfileOps.RemoveWidget(_profile.ActivePage, _selectedWidget);
            ApplyProfileMutation(ProfileMutationShape.Transform, null);
        }
    }

    /// <summary>
    /// Delete removes the selected widget — except while typing in an
    /// inspector text box, where Delete edits the field. Backspace NEVER
    /// deletes: a field that momentarily lost focus (inspector rebuild)
    /// would otherwise lose the selection while the user corrects text.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object _, KeyEventArgs e)
    {
        if (MainWindowInputPolicy.ShouldHandleDeleteKey(e.Key, Keyboard.FocusedElement is TextBox))
        {
            DeleteSelectedWidget();
            e.Handled = true;
        }
    }

    #endregion

    #region Catalog, Header, and Action Handlers

    private void TxtSearchCatalog_TextChanged(object _, TextChangedEventArgs e) => RefreshCatalog();

    /// <summary>One catalog sort, three call sites: the initial fill, the
    /// filter box, and the empty-query reset all render through this.</summary>
    private void RefreshCatalog()
    {
        ListCatalog.ItemsSource = CatalogFilter.Apply(_loader.RegisteredPlugins, TxtSearchCatalog.Text.Trim());
    }

    private void BtnPlaceWidget_Click(object sender, RoutedEventArgs _)
    {
        if (sender is Button btn && btn.Tag is string pluginId)
        {
            var placed = ProfileOps.PlaceCentered(_profile, _loader, this, pluginId);
            if (placed == null) return;

            ApplyProfileMutation(ProfileMutationShape.Transform, placed);
        }
    }

    private void RenamePage(int index)
    {
        if (index < 0 || index >= _profile.Pages.Count) return;
        var page = _profile.Pages[index];

        string? newName = _dialogHost.PromptForText($"Rename Page", $"New name for '{page.PageName}':", page.PageName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        ProfileOps.RenamePage(page, newName);
        ApplyProfileMutation(ProfileMutationShape.Structural, _selectedWidget);
    }

    private void SwitchToPage(int index)
    {
        if (!ProfileOps.SetActivePageIndex(_profile, index)) return;
        ApplyProfileMutation(ProfileMutationShape.Structural, null);
    }

    /// <summary>
    /// The window's page-delete seam: the single UI gate (the module's
    /// last-page rule), a bounds-safe read of the confirm facts, and the
    /// delete + structural refresh. Internal so tests can pin that a stale
    /// index degrades to a no-op instead of throwing in the window.
    /// </summary>
    internal void DeletePage(int index)
    {
        // One delete gate: the module's rule, the same predicate the tab
        // strip's button-enablement consults. The page's facts for the
        // confirm read through the module's bounds-safe accessor, so a stale
        // index is a silent no-op here, not a throw ahead of DeletePage's
        // own validation.
        if (!ProfileOps.CanDeletePage(_profile)) return;

        if (ProfileOps.TryGetPage(_profile, index) is not { } targetPage) return;
        if (targetPage.Widgets.Count > 0 && !_dialogHost.Confirm("Delete Page", $"Are you sure you want to delete '{targetPage.PageName}' containing {targetPage.Widgets.Count} widget(s)?"))
            return;

        if (!ProfileOps.DeletePage(_profile, index)) return;
        ApplyProfileMutation(ProfileMutationShape.Structural, null);
    }

    private void BtnAddPage_Click(object _, RoutedEventArgs e)
    {
        ProfileOps.AddPage(_profile);
        ApplyProfileMutation(ProfileMutationShape.Structural, null);
    }

    private void ChkSnapToGrid_Changed(object _, RoutedEventArgs e)
    {
        if (!_wired) return;
        _profile.ActivePage.SnapToGrid = ChkSnapToGrid.IsChecked == true;
        ApplyProfileMutation(ProfileMutationShape.Transform, _selectedWidget);
    }

    private void ChkEditMode_Changed(object _, RoutedEventArgs e)
    {
        if (!_wired) return;
        _compositor.IsEditMode = ChkEditMode.IsChecked == true;
        SkiaCanvas.InvalidateVisual();
    }

    private void ExportProfile()
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

    private void ImportProfile()
    {
        var dlg = new OpenFileDialog { Filter = "Display Profile (*.json)|*.json" };
        if (dlg.ShowDialog() == true)
        {
            // The import boundary is ProfileOps' one funnel: the size guard
            // (before any read) and the parse are owned there; the window
            // maps the named verdicts to its own surface.
            switch (ProfileOps.ImportProfileFile(dlg.FileName, _loader, this))
            {
                case ProfileImportOutcome.Loaded(var loaded):
                    try
                    {
                        // The close behavior travels with the JSON, but an
                        // imported profile lacking it (null, "no opinion")
                        // must not drop the local value: the merge re-stamps
                        // the local close behavior onto the imported profile
                        // before the swap, so the next export carries it.
                        loaded.CloseBehavior = CloseBehaviorPolicy.MergeImport(loaded.CloseBehavior, _profile.CloseBehavior);

                        // One swap site: ReplaceProfile disposes the old profile's
                        // widget instances and returns the imported profile active.
                        _profile = ProfileOps.ReplaceProfile(_profile, loaded);

                        // The funnel owns everything after the swap — tab strip,
                        // picker, and the snap-to-grid resync (a RawWrite): the
                        // old force-write of the checkbox is gone.
                        ApplyProfileMutation(ProfileMutationShape.RawWrite, null);
                    }
                    catch (Exception ex)
                    {
                        _dialogHost.Error("Import Error", $"Error importing profile: {ex.Message}");
                    }
                    break;
                case ProfileImportOutcome.TooLarge:
                    _dialogHost.Error("Import Error", "The selected profile file is too large to import.");
                    break;
                case ProfileImportOutcome.Failed(var detail):
                    _dialogHost.Error("Import Error", $"Error importing profile: {detail}");
                    break;
                // Absent: a delete between the dialog and the read is a
                // benign no-op, the file the dialog handed back is gone.
                case ProfileImportOutcome.Absent:
                    break;
            }
        }
    }

    private void BtnClear_Click(object _, RoutedEventArgs e)
    {
        if (_dialogHost.Confirm("Confirm Clear", "Are you sure you want to clear all widgets from the current page?"))
        {
            ProfileOps.ClearPage(_profile.ActivePage);
            ApplyProfileMutation(ProfileMutationShape.Transform, null);
        }
    }

    #endregion


    // The badge's last applied state (null until the first tick): the per-tick
    // work (the label/brush pair + the resource lookup) runs only when the
    // engine's state actually changes, not 30 times a second per identical
    // state. State compare is equivalent to the old (label + brushKey)
    // string compare — the mapping is injective (Connecting and Disconnected
    // share a brush, but not a label).
    private ConnectionState? _lastBadgeState;

    private void UpdateUsbBadge()
    {
        var state = _usbDevice.State;
        if (state == _lastBadgeState) return; // state unchanged — skip the per-tick resource lookup
        _lastBadgeState = state;

        var (label, brushKey) = UsbBadgeModel.From(state);
        var resources = Application.Current.Resources;
        UsbStatusDot.Fill = (Brush)resources[brushKey];
        TxtUsbStatus.Text = label;
    }

    private void BtnSettings_Click(object _, RoutedEventArgs e)
    {
        ShowSettingsDialog();
    }

    /// <summary>
    /// The tray/second-launch activation: shows a hidden window (restoring
    /// it from the tray hide) and brings it forward. Both callers arrive on
    /// the UI thread (the tray icon's events are WPF-routed; the
    /// single-instance guard hops through the App's dispatcher).
    /// </summary>
    internal void ShowFromTray()
    {
        if (!IsVisible)
        {
            WindowState = WindowState.Normal;
            Show();
        }
        Activate();
    }

    /// <summary>
    /// The tray menu's "Quit" (ADR-0018): the explicit-quit path through the
    /// normal close sequence (OnClosing, the teardown plan, the display
    /// standby), then an explicit Shutdown. A hidden window's Close does not
    /// trip OnLastWindowClose, so without the Shutdown the process would
    /// linger with its icon already gone; WPF's Shutdown is idempotent, so
    /// the visible-window case (where OnLastWindowClose already scheduled
    /// the shutdown) stays a single shutdown.
    /// </summary>
    internal void QuitFromTray()
    {
        QuitClose();
        // Close() on the visible window already scheduled the shutdown
        // through OnLastWindowClose, and WPF's Shutdown is idempotent when
        // the shutdown is in flight - so the call below is safe either way.
        // It is the ONLY thing that ends a process whose only window was
        // hidden: a hidden window's Close does not trip OnLastWindowClose.
        Application.Current?.Shutdown();
    }

    /// <summary>
    /// The explicit-quit close (ADR-0018): the veto flag lands before the
    /// close so the close intercept (which hides to the tray when the
    /// behavior is on) vetoes itself and the tray's Quit always exits. Named
    /// apart from the tray caller so the test host - whose own Application
    /// must survive - can drive the veto + close without the shutdown.
    /// </summary>
    internal void QuitClose()
    {
        _quitting = true;
        Close();
    }

    /// <summary>
    /// The close intercept (ADR-0018): a window close (X, Alt+F4) hides to
    /// the tray instead of closing when the resolved close behavior is the
    /// tray keep-alive and the tray icon is live. With the behavior on and
    /// the tray dead (N1) the close falls through to the normal exit: a
    /// hidden window with no tray is unreachable, and losing the app is
    /// worse than leaving it.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_wired || _quitting)
        {
            return;
        }
        if (CloseInterceptPolicy.ShouldHide(_profile.CloseBehavior, _tray.IsLive))
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// The minimize intercept (ADR-0018, M2): a minimize hides to the tray
    /// under the same policy as a close, so the window never lingers as a
    /// minimized taskbar entry the user would have to restore.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (!_wired || WindowState != WindowState.Minimized)
        {
            return;
        }
        if (CloseInterceptPolicy.ShouldHide(_profile.CloseBehavior, _tray.IsLive))
        {
            Hide();
        }
    }

    /// <summary>
    /// The session-end standby (ADR-0018): the production caller is the
    /// App's SessionEnding event (wired by the CloseIntercept step). A
    /// system shutdown or logoff kills the process mid-frame-stream, and
    /// this is the one chance to run the display's standby ritual before
    /// the process dies. Routes through the injectable seam (the test
    /// probe) or the engine's real non-disposing seam, and returns the
    /// truthful verdict.
    /// </summary>
    internal bool RunSessionEndStandby()
        => _sessionEndStandby is { } probe ? probe() : _usbDevice.TryGoToStandby();

    /// <summary>
    /// Opens the settings hub (ADR-0018): the close-behavior radios read the
    /// profile's raw persisted value and write it back through
    /// <see cref="CommitCloseBehavior"/> the moment they are checked; the
    /// Profile group's export/import buttons route to the window's file
    /// flows.
    /// </summary>
    private void ShowSettingsDialog()
    {
        new Dialogs.SettingsDialog(
            this,
            _themeApplicator,
            currentCloseBehavior: _profile.CloseBehavior,
            onCommitCloseBehavior: CommitCloseBehavior,
            onExportProfile: ExportProfile,
            onImportProfile: ImportProfile).ShowDialog();
    }

    /// <summary>
    /// The close-behavior write-through from the settings hub (ADR-0018):
    /// the radio's check is the change, so the profile is committed and
    /// marked dirty in place - no Apply step to forget. The canvas is
    /// untouched (the setting is not a page/widget mutation), so no
    /// mutation-shape refresh runs.
    /// </summary>
    private void CommitCloseBehavior(string value)
    {
        _profile.CloseBehavior = value;
        _profilePersistence.MarkDirty();
    }

    private static void Log(string msg) => FileLog.Write(msg);
}
