# Telemetry widget recovery: AIDA64 freeze, HWiNFO picker, spontaneous exit

Status: approved (user instruction, 2026-11-11). Two design-review passes and three drift passes were run in-session; their findings are folded in. Code line numbers in the root-cause sections refer to the pre-fix tree at v0.9.3 (9c3295c).

## Problem

Three reports from users on a v0.9.3 build:

1. The app exits on its own after an unpredictable interval (30 s to 30 min).
2. Adding the AIDA64 Panel widget freezes the whole app until the widget is removed.
3. The HWiNFO Sensor widget has no sensor dropdown in the inspector.

Each is diagnosed below against this tree. Two have a code-level root cause found
by reading the call paths. The first does not: the evidence available here rules
out every exit path the code owns, so the fix is a truthful exit record plus the
one defect found in the close path.

## Evidence gathered

- `%LOCALAPPDATA%\ModernWigiDash\display_device.log` (5 MB + rotated 5 MB): two
  complete sessions on 2026-11-10 and 2026-11-11. Both end with
  `[App] === Application exiting ===` after a full `[TEARDOWN] plan complete`.
- No `crash.log` exists. `AppDomain.UnhandledException` and
  `DispatcherUnhandledException` both write it (`App.xaml.cs:59-73`), so no
  managed exception escaped either handler.
- No Application-log `Application Error` / `.NET Runtime` event for
  `ModernWigiDash.App.exe` in the last 60 days. WER has `AppCrash_ModernWigiDash*`
  archives only from 2026-08-09 and 2026-08-26, before v0.9.3.
- The steady-state log shows a healthy display loop (USB writes ~49 ms, 30 FPS
  frame deliveries) right up to the teardown line.

The pre-fix build was not run against the wedged endpoint. The causal evidence is
the lock analysis plus a regression test that fails on the pre-fix shape (a
second `Start` waits on the gate the poll loop holds); the fixed build's device
run is the proof that the UI no longer waits.

Conclusion: on this machine the process reaches WPF's normal `Exit`. Something
requests a window close, and the request is not recorded anywhere. A hard native
crash would leave no exit marker but would leave a WER entry; none exists.

## On-device verification (2026-11-11)

The fixed build ran elevated against the physical WigiDash
(`USB\VID_28DA&PID_EF01\395E384E3230`), with `WigiDashService` stopped and a
loopback listener on `127.0.0.1:8733` that accepts and never answers (a wedged
vendor call). The app placed the AIDA64 Panel and then the HWiNFO Sensor.

- `[AIDA-MASTER] polling started` at 22:48:36.682, then
  `TimeoutException ... timed out attempting to send after 00:00:10` at
  22:48:46.778: the poll loop sat inside the vendor call for the full send
  timeout.
- `[HW] [FrameDelivery] Frame #300 sent` (22:48:38.320) and `#360` (22:48:43.949)
  landed during that hang, and UIA `doctor` answered in 43 ms, then 27 ms during
  the next hang. The UI thread and the 30 FPS compose path were not blocked.
- With the installed AIDA64 Extreme running (its G.SKILL LCD module enabled),
  the app logged `[AIDA-MASTER] registered slot 0 (1016x592, 5x4)` and the
  widget rendered AIDA64's published frame (its default blue LCD panel), so
  registration, publish, map read, bitmap build, and draw are device-proven. UIA
  `doctor` answered in 400 ms in that sample; it followed the placement and the
  first frame, so it includes the one-time inspector panel and bitmap work, while
  the steady-state samples are the 22-43 ms values above. The panel content is
  AIDA64's default because no LCD items are configured on this machine; the
  widget draws whatever AIDA publishes.
- The HWiNFO inspector rendered the empty-state hint (wording at the time:
  `No HWiNFO sensors detected. Make sure HWiNFO and the G.SKILL WigiDash service are running, then reopen settings.`;
  the shipped wording dropped the reopen advice because the inspector now
  refreshes itself) and no Sensor text box.
- A clean close wrote `[SHELL] window closed (reason=user-close, trayLive=True)`
  and `[App] === Application exiting === reason=user-close`.
- A later cycle with both widgets and the wedged endpoint measured the close
  path after the teardown fix: `[SHELL] window closed` -> `[TEARDOWN] plan
  complete` in 87 ms, `wmd-verify stop` in 378 ms (the same cycle was ~17.5 s
  before).

- Against the real, running vendor service (no wedged endpoint), the HWiNFO
  Sensor row rendered a `ComboBox` with `Automatic (first available sensor)`
  selected and the widget drew a live gauge (`System: ASUS, 13871.0 MB`). The
  embedded provider in `WigiDashService` serves the list, so the populated path
  is device-proven; the empty-state proof above came from the wedged-endpoint
  cycles, where the same row rendered the hint.
- The list contents (key-to-label mapping, the 128-character label bound) are
  pinned by `GetPropertyOptions_ListsSensorKeysAndLabels`,
  `GetPropertyOptions_OverlongSensorName_TruncatesTheLabel`, and
  `GetPropertyOptions_PersistedKeyNotInTheList_PrependsANotAvailableEntry`; the
  device run proves the inspector builds the dropdown when options exist.

## Root cause 1: AIDA64 Panel freezes the UI thread

`AidaPanelWidget.Render` runs on the 30 FPS compose tick and calls
`_master.Start()` every frame (`ModernWigiDash.Widgets/AidaPanelWidget.cs:52-61`,
`70-76`). `Start()` takes the master's `_gate` to test and create the loop
(`ModernWigiDash.Hardware/Aida64/AidaPanelMaster.cs:125-142`).

The poll loop holds that same `_gate` across vendor I/O:

- `EnsureRegistered` holds it across `_writer.TryInit` and `_writer.TryWrite`
  (`AidaPanelMaster.cs:176-201`). Those reach the vendor WCF service
  (`ModernWigiDash.Hardware/Service/VendorAidaMmapWriter.cs:15-52`), whose binding
  allows a 5 s open and a 10 s send timeout (`WigiDashServiceClient.cs:193-206`).
  The ready probe runs before every tick (150 ms cadence), backed off to one
  attempt per 500 ms while it returns false (`PollLoop.cs:83-88`), and keeps
  running until slot 0 registers.
- `PollOnce` holds it across the heartbeat write, two map reads, the full frame
  read and the ack (`AidaPanelMaster.cs:208-247`).

So the render tick blocks on a lock held by a thread that can be inside a
multi-second network call. With AIDA64 absent, wedged, or the vendor service
slow, the UI thread spends its time waiting on `_gate` and the app stops
responding. Removing the widget also routes through `AidaPanelService.Release`
to `Dispose`, which takes `_gate` (`AidaPanelMaster.cs:158-168`), so removal
inherits the same stall.

Placeholder handling is not the problem: when no frame is published, `Render`
draws the placeholder with no vendor call.

### Fix 1 (design)

Lock discipline only, no protocol change. Three gates, one owner each:

- `_startGate` guards `_disposed`, `_started` and `_loop`. `Start()` uses a
  volatile fast path, then takes `_startGate`, re-checks, and **starts the loop
  inside the gate**. Starting outside it lets `Dispose` capture a not-yet-started
  loop (a no-op stop) while the render thread then starts a loop nobody owns.
  `Dispose` sets `_disposed` and captures `_loop` under the same gate, then stops
  outside it (the join must not run under the gate).
- `_gate` guards `_registered`, `_masterCounter`, `_lastError`, and the
  `_failureDedup` resets, and reads `_disposed` in its short sections. The dedup
  object is poll-thread-only; its reset calls sit inside the commit sections for
  locality. `EnsureRegistered` and `PollOnce` take the gate in short sections and
  perform every vendor call outside it.
- No path holds two gates at once. Every section releases one before taking
  another, and the loop stop runs after `_startGate` is released, so the three
  gates cannot deadlock by nesting.
- `_readerGate` guards the map reads and `_reader.Dispose()`, so teardown waits
  for one map-read section (two reads, each bounded by the 100 ms mutex budget,
  plus one payload copy) rather than a WCF timeout. Every `_readerGate` section
  re-checks `_disposed`, so no I/O starts after disposal.

Post-I/O re-checks: after each vendor write, `EnsureRegistered` re-checks
`_disposed` before the next call and before committing `_registered`;
`PollOnce` re-checks before the ack. The guarantee is per section: no I/O starts
after teardown, and a write already past its check may still land once. A poll
thread that returns from a 10 s send after teardown therefore commits nothing and
does not start the next leg.

`AidaPanelWidget.DisposeAsync` runs on the caller's (UI) thread. The shared
master's `Dispose` joins the poll loop, and `LoopLifetime` allows a 5 s join
(`PollLoop.cs:21`) that a tick inside vendor I/O cannot shorten. The borrowed
path therefore hops `AidaPanelService.Release` to the thread pool, once per
borrow: `DisposeAsync` does not await it, a second call is a no-op, and a process
exit may skip the join (the OS reclaims the handles). To keep the vendor's
single slot single-owner, `AidaPanelService.Acquire` makes the create decision
under the gate and waits on a manual-reset gate while a release is in flight
(each wait at most 6 s, then it re-checks), so a master is never created beside
a disposing one. That wait only happens on a remove-and-re-add during a
teardown, it is bounded, and it is the deliberate allow-list entry in
`DebtGuardTests` (`AidaPanelService.cs`: the alternative is two masters clearing
each other's frame markers).

`AidaPanelMaster.Stop()` was deleted: it had no production caller, and its
stop-then-start window could create a second poll loop on the same slot.

`_slaveAlive`, `_slaveBaseline`, `_ringIndex`, `_generation`, `_masterCounter`
mutations stay on the poll thread; `_published` stays `Volatile`. No new
public surface.

Second serialization with its own consequence: before phase 1,
`WigiDashServiceClient._gate` (`WigiDashServiceClient.cs:37`) was held across
`TryInitAidaProvider`/`TryWriteAidaMmap` as well as across the HWiNFO
`RefreshCache` (`:398-469`). An AIDA call that hung therefore blocked the
HWiNFO list fetch too. The AIDA operations now capture `_channel` under `_gate`
and call outside it, and a concurrently closed channel surfaces as the existing
caught failure.

### Tests (fix 1)

- `Start_WhileTheVendorIoIsBlocked_ReturnsWithoutWaiting`: a fake writer whose
  `TryInit` blocks; start the master, wait until the writer is inside, then
  assert a second `Start()` completes inside 2 s. It is the regression pin for
  the render-tick stall (the old code waited on `_gate` there).
- `EnsureRegistered_DisposedDuringTheProviderInit_WritesNoSlot`: a call that
  returns after teardown writes nothing and never registers slot 0.
- A `Dispose` latency test was dropped deliberately: the master's own `Dispose`
  still joins the poll loop (bounded 5 s) by design. The UI-facing guarantee is
  the widget's off-thread release, so a test asserting a fast master-level
  `Dispose` would assert a contract the code does not offer.
- Existing `AidaPanelMasterTests` / `AidaPanelWidgetTests` stay green; the
  protocol bytes and order are unchanged.

## Root cause 2: no HWiNFO sensor dropdown

The property is a dynamic `Choice` (`ModernWigiDash.Widgets/HwinfoWidget.cs:21-23`)
whose options come from the vendor client's background cache
(`HwinfoWidget.cs:78-104`). `SensorValuesFacet.GetSensorList` returns the cache
and, on a cold or stale cache, only *schedules* a refresh
(`WigiDashServiceClient.cs:503-517`). The first call therefore returns null.

The inspector asks once, at panel build:

1. `ResolveOptions` sees an empty dynamic list and falls back to the attribute's
   static options, which are empty
   (`ModernWigiDash.App/Inspector/InspectorModelBuilder.cs:80-95`).
2. `BuildPropertyEditor`'s Choice branch requires `desc.Options.Count > 0`
   (`ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs:147`). With zero
   options the switch falls to `default:` and builds a plain text box
   (`InspectorPanelRenderer.cs:193-196`).

A text box holding a coded key like `1:2:510` is what a user reads as "no
dropdown". Nothing rebuilds the panel when the sensor list later arrives, so the
text box persists for the life of the selection. The same path produces a text
box when the vendor service is absent, with no indication that the service is
the reason.

### Fix 2 (design)

Reuse check first: the repo already has a sensor dropdown with an empty-state hint
(`WidgetPropertyType.SensorSelector` → `InspectorPanelRenderer.BuildSensorSelector`,
`:372-381`). It does not fit unchanged: its options are pushed in by the App from
the shared LibreHardwareService store (`InspectorModelBuilder.SensorOptions`) and
its persisted value is a reading label. The HWiNFO widget pulls its options from
the vendor client and persists `rt:id1:id2`. The empty-state *shape* is reused;
the option source is not.

- `WidgetPropertyAttribute` gains `bool RequiresListedValue { get; init; }`
  (default false). `EditorDescription` gains the same non-positional init
  property plus an `init` `Description` (before this change the attribute's
  `Description` reached no editor). `HwinfoWidget.SensorKey` sets the flag and
  carries the sentence `No HWiNFO sensors available yet. The G.SKILL WigiDash
  service provides the list; start it if it is not running.`
- `InspectorPanelRenderer`'s Choice branch: when `RequiresListedValue` is set and
  options are empty, call the shared empty-state builder (extracted from
  `BuildSensorSelector`): `TextBlock`, 11 pt, `TextSecondary`, wrapped, using the
  property description. The store-backed `SensorSelector` keeps its own service
  instruction as the builder's fallback, so the Hardware Monitor's empty state
  does not lose "Start LibreHardwareService". When options exist, render the
  ComboBox as today. Free-text Choice stays available to every other widget
  (Twitch channel entry relies on it).
- `VendorSensorRefresh` exposes `HasSensorList` (the last list fetch returned at
  least one sensor). `HwinfoWidget.Render` compares it across ticks and calls
  `Context?.RequestInspectorRefresh()` once, on the first render that observes a
  non-empty list. A widget placed after the cache is warm therefore requests one
  redundant rebuild; the widget cannot know whether the panel was built with
  options, and the cost is one panel rebuild at placement. The host's
  `OnUiThread` executes inline when already on the UI thread, so that rebuild
  runs in the render pass once, not per frame. No revert leg: a failed refresh
  keeps the last good list (`PublishSnapshot` keeps `_cachedSensors`), while a
  successful zero-sensor fetch replaces it with an empty list, so availability
  can go false again; the widget's one-way latch deliberately ignores that
  direction (the hint is a cold-cache / absent-service state, and a live
  zero-sensor list needs no inspector rebuild). Because the latch is one-way, no
  rate bound is needed.
- `HwinfoWidget.GetPropertyOptions` prepends `Automatic (first available
  sensor)` with the empty value, matching the refresh core's empty-pick
  fallback. A fresh widget's combo then shows the behavior it renders instead of
  a blank selection beside a live reading, and a configured widget can return to
  the fallback.
- Any non-empty stored value no current sensor matches (a service restart, a
  renamed sensor, or text the old free-form editor allowed) is prepended as
  `<value> (not available)` and stays selected, so the old text box's
  information is not lost to a blank combo.
- `GetSensorList` returns null on a cold cache, or when a refresh has failed
  before any list was ever cached; otherwise the last good list. A later panel
  build over a reachable zero-sensor list renders the automatic-only combo; a
  panel already built keeps the hint by design (no arrival transition).

### Tests (fix 2)

- `Describe_RequiredChoiceWithoutOptions_CarriesTheFlagAndTheDescription` and a
  renderer test asserting the empty state (TextBlock, no TextBox) plus the hint
  text.
- A renderer test asserting a non-required Choice with no options stays a text
  box (the Twitch behavior is preserved).
- `Render_SensorListArrives_RequestsInspectorRefreshOnce` on `HwinfoWidget`,
  using the existing fake sensor facet and a counter added beside `Renders` in
  the shared `TestContext` (`TestDoubles.cs:31,49`).
- `GetPropertyOptions_ListsSensorKeysAndLabels` pins the automatic first entry
  and the two sensor keys after it.
- `GetPropertyOptions_PersistedKeyNotInTheList_PrependsANotAvailableEntry`,
  `GetPropertyOptions_UnparseableStoredValue_IsAlsoKeptVisible`, and
  `GetPropertyOptions_OverlongSensorName_TruncatesTheLabel` pin the stale-pick
  entries and the 128-character label bound.
- `Render_SensorSelectorWithoutOptions_KeepsTheServiceInstruction` pins the
  Hardware Monitor fallback text through the shared builder.
- The renderer tests construct `EditorDescription` positionally
  (`KeyCaptureEditorTests.cs:218`); the new fields are init-only on the record
  body, so that construction keeps compiling.

## Root cause 3: the exit trigger was never recorded

Every close the code owns is silent:

- The close intercept hides or falls through without a line
  (`ModernWigiDash.App/WindowLifecycle.cs:79-90`).
- Tray Quit sets the latch and closes (`WindowLifecycle.cs:29-45`).
- The update restart closes the window (`MainWindow.Update.cs:95-116`).
- `Application.SessionEnding` only runs the standby; nothing records the cause.

`[App] === Application exiting ===` proves the process reached `Exit`; it does
not say what asked for the close. So a report cannot be triaged, and this tree
cannot reproduce it.

### Fix 3 (design)

- `ProcessLifecycle` gains `ExitReason` (volatile string, one owner, alongside
  the two existing process flags).
- Record it at the five paths that can end the process through the app's own
  close:
  - `AppShellLifecycle.QuitFromTray` → `tray-quit`.
  - `SystemEvents.SessionEnded` (the committed shutdown, not the vetoable query
    event) → `session-end`, which also writes
    `[SHELL] session ended (reason=session-end)` and flushes at the recording
    site, because the process may be terminated before `Closed` or `Exit` runs.
    Recording at `Application.SessionEnding` would latch a reason a vetoed logoff
    never used, mislabeling every later close.
  - `MainWindow.Update.ShowRestartPrompt` → `update-restart`.
  - `App.OnStartup`'s secondary-launch branch → `second-launch`. That process
    exits before a window exists, so the close line never runs (the drift
    review's catch).
  - `MainWindow`'s close handler, after the intercept declines to hide, when no
    reason is set → `user-close`.
- `AppShellLifecycle` wires the tray controller's quit delegate through its own
  recording `QuitFromTray`, not straight to `WindowLifecycle.QuitFromTray`
  (otherwise the menu path records nothing and the marker says `user-close`).
- The standing-close attribution is a pure rule
  (`ExitReasonPolicy.ForClose`, four tests): a cancelled close records nothing,
  a recorded cause wins, an unattributed standing close is `user-close`.
- `MainWindow`'s `Closed` handler writes
  `[SHELL] window closed (reason=<trigger>, trayLive=<bool>)` before the teardown
  plan runs. `App.WriteExitMarker` appends ` reason=<trigger>` when set; the
  existing marker prefix stays byte-compatible (`TeardownPlanTests.cs`).
- `AppDomain.UnhandledException` also writes `[App] === FATAL unhandled === <type>`
  to the display log and flushes. A hard managed crash therefore leaves a line
  even when the reporter sends only `display_device.log`.
- Defect found while tracing this path and fixed here: the update restart ended
  in a bare `Close()`. With close-behavior `hideToTray` and a live tray,
  `OnWindowClosing` cancelled the close and hid the window, so the
  already-launched updater waited for a process that never exited and the update
  never installed. It now routes through the explicit-quit latch (`QuitClose`),
  which already exists for exactly this reason and is pinned by
  `OnWindowClosing_WhenQuitCloseInvoked_BypassesHideIntercept`.

This is instrumentation plus one real close-path defect, not a guess at the
reported cause. The five triggers cover every close the app initiates (native
faults, external termination, and machine death are the uncovered remainder,
which the triage table lists); the next report will therefore say which one
fired. A hard native crash still cannot write a line, so a report that ends
without an exit marker and without a FATAL line is itself evidence of a native
fault, and the WER entry on that machine is then the place to look.

Tests: `ExitReasonPolicyTests` (the four attribution cases),
`RecordSessionEnded_WritesTheReasonAndFlushes` (the committed-shutdown line), and
`WriteFatalLine_WritesTheTypeToTheDisplayLog` (the hard-fault line). The
production tray menu is pinned by `TrayQuit_ArmsLatch_ClosesWindow_AndShutsDown`
(it asserts the recorded cause), and the wrapper by
`QuitFromTray_RecordsTheTrayQuitReason`; the update-restart and
second-launch assignments are single call sites, and the update restart's close
semantics are pinned by `OnWindowClosing_WhenQuitCloseInvoked_BypassesHideIntercept`.
The three device cycles exercised `user-close` end to end.

## Root cause 4 and fix: teardown waited on the vendor client

`WigiDashServiceClient.Dispose` took `_gate`, and a background `RefreshCache`
holds that gate across its WCF calls (10 s send/receive timeouts). The teardown
plan's `VendorService` step runs `Dispose` on the UI thread, so app close waited
out whatever call was in flight, then spent the 5 s `CloseTimeout` closing the
channel. On-device that was ~17.5 s from `[SHELL] window closed` to
`[TEARDOWN] plan complete` with a wedged endpoint.

The fix keeps blocking semantics out of the close path:

- `Dispose` is idempotent through `_disposeOnce` (Interlocked), sets the
  volatile `_disposed`, atomically detaches `_channel`/`_factory` with
  `Interlocked.Exchange`, and `Abort()`s them outside every lock. `Abort` is
  immediate; `Close` can spend the 5 s close timeout and has no value at process
  exit.
- `EnsureChannel` refuses after disposal, and `EnsureConnectedLocked` re-checks
  `_disposed` after the provider init so a channel opened during teardown is
  closed by the attempt that created it.
- `RefreshCache` is unchanged: it may still hold `_gate` across I/O, but nothing
  on the close path waits for that gate anymore. A call in flight faults into
  the existing catch when the channel is aborted.

Tests: `Dispose_WhileASensorReadIsInFlight_DoesNotWaitOnTheVendorCall` (a fake
channel blocks `GetSensorList`; `Dispose` must return inside 2 s, where the old
code queued behind `_gate`).

## Deliverables and docs touched

- Code: `ModernWigiDash.Hardware/Aida64/AidaPanelMaster.cs`,
  `ModernWigiDash.Hardware/Aida64/AidaPanelService.cs`,
  `ModernWigiDash.Widgets/AidaPanelWidget.cs`,
  `ModernWigiDash.Hardware/Service/WigiDashServiceClient.cs`,
  `ModernWigiDash.Widgets/HwinfoWidget.cs`,
  `ModernWigiDash.Widgets/VendorSensorRefresh.cs`,
  `ModernWigiDash.Sdk/Attributes.cs`,
  `ModernWigiDash.App/Inspector/{EditorDescription,InspectorModelBuilder,InspectorPanelRenderer}.cs`,
  `ModernWigiDash.App/{ProcessLifecycle,ExitReasonPolicy,AppShellLifecycle,App}.cs`,
  `ModernWigiDash.App/MainWindow.xaml.cs`, `ModernWigiDash.App/MainWindow.Update.cs`.
- Tests: `ModernWigiDash.Tests/{AidaPanelMasterTests,AidaPanelServiceTests,WigiDashServiceClientTests,HwinfoWidgetTests,InspectorModelBuilderTests,InspectorPanelRendererTests,TeardownPlanTests,AppShellLifecycleTests,TestDoubles,DebtGuardTests,ExitReasonPolicyTests}.cs`.
- User docs: `README.md` (Vendor Integration, HWiNFO Sensor, AIDA64 Panel,
  Profile Persistence rows); `release/README.txt` (the v0.9.4 section).
- Domain docs: `CONTEXT.md` (`WigiDashServiceClient`, `HWiNFO widget`,
  `AIDA64 panel widget`, `AidaPanelMaster`, new `ExitReasonPolicy`, `PollLoop`
  rows).

## Reporter triage (spontaneous close)

The instrumentation only helps if a report carries the log. Ask the reporter
for, in this order:

1. `%LOCALAPPDATA%\ModernWigiDash\display_device.log` and `.log.1`, plus
   `crash.log` (and `.log.1`) if present, copied **before relaunching** (the log
   rotates at 5 MB, so the failed session can be overwritten).
2. The local launch and close times, with the UTC offset (the log stamps UTC).
3. Application-log entries naming `ModernWigiDash.App.exe` (`Application
   Error`, `.NET Runtime`, `Windows Error Reporting`).
4. The newest `C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_ModernWigiDash*`
   folder, if readable.
5. Five context facts: window visible or hidden, widgets on the page, any
   update prompt, an OS restart/logoff, a second launch.

`calendar-credentials.bin` must never be sent; `profile.json` only when the
layout matters.

| Log evidence | Reading |
|---|---|
| `[SHELL] window closed (reason=user-close, ...)` | A standing close with no earlier cause. If nobody touched it, an external process or the OS sent WM_CLOSE. |
| `reason=tray-quit` | The tray Quit path ran. |
| `reason=session-end` | The OS reported a committed logoff or restart. |
| `reason=update-restart` | The update restart path ran (it now exits to install). |
| `reason=second-launch` | A second instance started; this one exited as the secondary. |
| `[App] === FATAL unhandled === <type>` | A managed exception escaped both handlers; `crash.log` names it. |
| No marker, no FATAL, WER `AppCrash` present | Native fault; the WER exception code and module name the culprit. |
| No marker, no FATAL, no WER | The process was terminated externally or the machine died. |

The evidence artifacts under `%TEMP%` are per-run and are not a permanent record.
The decisive lines are quoted inline above; the recipe is the three-cycle device
run (wedged endpoint, then the real AIDA64/service run).

## Verification (final)

Build 0 warnings, 3021 tests, `dotnet format --verify-no-changes` clean.
On-device: three elevated wedged-endpoint cycles (the 10 s vendor hang with
frame deliveries and UIA `doctor` at 22-43 ms, plus the 60-87 ms close) and one
live-panel cycle with AIDA64 running and the HWiNFO dropdown populated from the
real service. Evidence artifacts:
`%TEMP%\opencode\wmd-verify-evidence\{aida-freeze,aida-freeze-final,aida-live,hwinfo-populated}\`.

## Out of scope

- No change to the AIDA64 handshake bytes or cadence (ADR-0026).
- No change to the vendor WCF binding's timeouts (ADR-0022); the fix removes the
  UI dependency on those timeouts, it does not shorten them.
- Guessing at the exit cause without a log from an affected machine. No
  affected-machine evidence exists in this repo or on this workstation: every
  local session ends cleanly, and no crash log or WER entry exists for the
  reported interval. The ask is concrete: from one reporter, take
  `%LOCALAPPDATA%\ModernWigiDash\display_device.log` and `crash.log` (if present)
  plus the newest `Application`-log `Application Error`/`.NET Runtime` entry for
  `ModernWigiDash.App.exe`. The decision after that: a named trigger maps to a
  named path; a missing marker plus a WER entry is a native fault, investigated
  from the WER stack.

## Phases (landed 2026-11-11)

1. AIDA64 lock discipline + the client's AIDA calls outside its gate + tests.
2. Inspector required-choice empty state + HWiNFO live refresh + tests.
3. Exit-reason recording + the update-restart latch fix + second-launch reason + tests.
4. Vendor-client teardown: lock-free detach + abort + the in-flight-read test.

Each phase left the tree buildable and independently revertable. Rollback:
phases 1 and 4 are internal. Phase 2 adds a public SDK attribute init property
(additive and source-compatible, but a revert removes the capability) and changes
the Hardware Monitor empty-state text. Phase 3 changes the log format the triage
table, README, and release notes quote, and reverting it also restores the update
restart's bare `Close()` (hide-to-tray swallows the install). Reverting phase 3
therefore undoes both the instrumentation and the update-restart fix.

## Risks

- The AIDA64 refactor moves I/O out of `_gate`; the guard against a disposed
  reader is the `_disposed` re-check inside `_readerGate`, and the post-I/O
  re-checks are what keep a late write from re-registering a torn-down master.
- `RequestInspectorRefresh` from the render tick is latched one-way in
  `HwinfoWidget`. A successful zero-sensor fetch can empty the client's list and
  the latch ignores that direction by design; if a future change wants the
  inspector to flip back to the hint live, it must add a reverse leg plus a rate
  bound.
- The update-restart latch fix changes behavior a user can observe (the app now
  exits to install instead of hiding). That is the documented intent of the
  restart prompt.
- `AidaPanelService.Acquire` can wait up to 6 s on a remove-and-re-add while the
  previous master's loop is inside a vendor call. That is the one deliberate
  blocking path in the change, bounded and allow-listed in `DebtGuardTests`; the
  alternative is a second master on slot 0.
- The HWiNFO list-arrival request runs inline on the render thread once per
  widget, rebuilding the inspector panel. It is outside the frame pipeline and
  not per tick, but a future change that makes it repeatable needs a rate bound.

