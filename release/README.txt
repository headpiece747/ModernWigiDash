ModernWigiDash — G.Skill WigiDash widget stack
===============================================

== Quick Start (3 steps) ==

  step 1.  Plug the display into a USB port   BEFORE starting the app.
  step 2.  Double-click  ModernWigiDash.App.exe
  step 3.  OPTIONAL telemetry (hardware / FPS widgets):
           right-click  setup-telemetry.bat  →  Run as administrator.
           (one time only — installs two background services)

  Done. The display shows your widgets. Nothing else needs installing.


== What's in this folder ==

  ModernWigiDash.App.exe       the app — just run this
  Resources/                   fonts, theme, icons (needed — keep it next to the exe)
  setup-telemetry.bat          optional: installs the telemetry services (Admin)
  README.txt                   this file
  LICENSE-ModernWigiDash.txt   the app's MIT license
  telemetry/                   bundled LibreHardwareService + PresentMon (see below)

== Do I need the telemetry? ==

   No. Everything works with just the app:
     - Clock, Stopwatch & Timer, Audio Visualizer, Now Playing, Twitch, Hotkey,
       Stock & Crypto, Picture & GIF Viewer, Weather Forecast, Calendar, Text,
       Steam Library — all fine.
     - Three widgets need a background service:
         Hardware Monitor   <- needs LibreHardwareService
         FPS / Frame Time   <- needs PresentMon Service
         HWiNFO Sensor      <- needs the vendor's WigiDash service, which the
         AIDA64 Panel          vendor's WigiDash Manager installs. The AIDA64
                               Panel also needs AIDA64 running with its LCD
                               output set to the WigiDash (AIDA64 / main menu /
                               File / Preferences / Hardware Monitoring / LCD).
   Without the services those widgets show a graceful "unavailable" state.
   The first two services are bundled and installed by  setup-telemetry.bat
   (Admin); they run in the background and start automatically with Windows.
   The vendor service comes from G.SKILL's WigiDash Manager (not bundled).

== First run ==

  - Add widgets: click a page in the editor, pick widgets from the catalog,
    drag / resize / rotate, and press the checkmark to save.
  - Swipe left/right on the display to switch pages.
  - Toggle the layout editor on/off from the app window.

== What's new in v0.9.2 ==

  - Physical Hardware Touch & Ergonomics:
      * Stationary finger hold resilience: continuous raw hardware digitizer polling
        no longer triggers re-entrancy or touch flapping.
      * Hardware touch target minimum floor (>= 44x44px) across Calendar chevrons,
        Now Playing controls, and To-Do Companion tasks.
      * Strict touch lifecycle: actions execute solely on TouchUp within bounds,
        preventing contact bounce triggers on TouchDown.
  - Media Session Cycling:
      * Reliable cycling across multiple sessions sharing the same browser or app
        (e.g., YouTube and SoundCloud tabs in Chrome).
  - Robust Calendar Feeds:
      * Universal auto-normalization of webcal:// schemes to https:// across UI input,
        profile imports, and JSON feed configurations.
  - Universal Widget Touch Contracts:
      * Architectural contract test suite covering all 10 touch-enabled widgets.
  - Release Pipeline Hardening:
      * Pre-flight XAML version validation and post-packaging sandbox smoke tests.

== What's new in v0.9.1 ==

  - Large Media & GIF Decoder:
      * Support for 100MB+ high-resolution animated GIFs with dynamic frame budgeting.
  - Physical Touch Gesture Interpreter:
      * Calibrated tap and drag arbitration for the physical WigiDash digitizer.
  - Real-Time Dynamic Version Display:
      * Dynamic version reporting from informational assembly metadata.

== What's new in v0.9.0 ==

  - Native Steam Library Widget:
      * Discover games automatically from local Steam installations (ACF manifests
        and VDF libraryfolders parser).
      * 3 distinct layout strategies: Hero Deck (horizontal scrolling carousel with
        live process playtime & hero banners), Showcase, and Poster Grid.
      * Fast zero-allocation Skia rendering with 24-slot LRU cached poster pipeline.
      * Live tracking of running Steam processes, one-touch launch / terminate.
      * Persona avatar and profile status display via Steam Community XML feed.
  - To-Do Companion Widget:
      * Cozy desktop dragon companion with task management, XP/affection progression,
        and integrated Pomodoro focus timer.
  - Swipe Navigation & Gesture Claimant Arbitration:
      * Page navigation on the display is now purely swipe-driven, removing accidental
        edge tap triggers.
      * Intelligent horizontal drag arbitration (IGestureClaimant) allows horizontally
        scrollable widgets (Steam Library, To-Do Companion, Calendar) to scroll
        smoothly without accidentally switching pages.
  - Deep Architecture Modernization:
      * Unified hardware lifecycle (DisplayDeviceSession) and app shell lifecycle
        (AppShellLifecycle).
      * Decoupled telemetry ingestion (TelemetryFeedCoordinator) and weather runtime
        engine (WeatherRuntimeEngine).
      * Modernized inspector editor registry and profile workspace architecture.


== Data locations & reset ==

  Profile + settings:  %LOCALAPPDATA%\ModernWigiDash\
  Theme:               app_theme.json  (same folder)
  Logs:                display_device.log + crash.log  (same folder)
  Twitch tokens:       DPAPI-encrypted, same folder (keyed to your Windows user)
  Reset:               close the app, delete that folder, relaunch.

== Troubleshooting ==

  "Display not connected" in the app bar?
    - The display must be plugged in BEFORE the app starts.
    - It needs the WinUSB driver bound. The vendor's WigiDash software
      installs it; otherwise bind it with Zadig:
        Device:  USB\VID_28DA&PID_EF01  ->  Driver: WinUSB  ->  Replace.
    - Unplug / replug after installing the driver, then restart the app.

  Windows SmartScreen shows "Windows protected your PC"?
    The release exe is unsigned. Click  More info -> Run anyway.

  Telemetry widgets still "unavailable" after setup-telemetry.bat?
    - Re-run the batch once (repair/upgrade; safe to repeat).
    - Check the services in services.msc: LibreHardwareService and
      "PresentMon Shared Service" — both should be Running / Automatic.

== Updating ==

  The app's built-in updater uses the separate app-only zip; never
  use that artifact for a fresh install (it has no telemetry installers).
  Download the new zip, unzip over the old folder (or delete it), and
  re-run setup-telemetry.bat once. Profiles and settings are kept.

== Uninstalling ==

  1. (If you installed telemetry) remove both programs:
     Settings -> Apps -> Installed apps ->
       "LibreHardwareService"     -> Uninstall
       "PresentMon Shared Service" -> Uninstall
  2. Delete the ModernWigiDash folder.
  3. (Optional) delete %LOCALAPPDATA%\ModernWigiDash to remove settings.
