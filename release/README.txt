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
    - Clock, Stopwatch, Audio Visualizer, Now Playing, Twitch, Hotkey,
      Stock & Crypto, Picture & GIF, Weather, Text  — all fine.
    - Two widgets need a background service, and only those two:
        Hardware Monitor  <- needs LibreHardwareService
        FPS / Frame Time  <- needs PresentMon Service
  Without the services those widgets show a graceful "unavailable" state.
  Both services are bundled and installed by  setup-telemetry.bat  (Admin).
  They run in the background and start automatically with Windows.

== First run ==

  - Add widgets: click a page in the editor, pick widgets from the catalog,
    drag / resize / rotate, and press the checkmark to save.
  - Swipe left/right on the display to switch pages.
  - Toggle the layout editor on/off from the app window.

== What's new in v0.6.8 ==

  - Settings hub: the title-bar gear opens one hub with three groups:
    Appearance (theme colors, page background), Behavior (close behavior,
    Start with Windows, hotkey kill switch), and Profile (export / import).
  - Hide to tray: opt in per profile (Behavior group). Close and minimize
    then hide instead of exit; the tray icon brings the window back and
    only the tray's  Quit  exits. A second launch activates the first
    window instead of opening a duplicate.
  - Start with Windows: per-user autostart (no admin needed). The
    autostarted instance opens minimized and keeps streaming frames.
  - Global hotkeys: the Hotkey widget can bind an OS-level chord
    (Ctrl/Alt/Shift/Win + key) that fires even while the app is hidden
    to the tray, including a  Flip page  action and a  Run AHK Script
    action that spawns your own AutoHotkey script (interpreter path set
    in the hub). The Behavior group's kill switch disables the global
    registration and the AHK spawn without touching any other action.
  - Theme: app_theme.json now lives in %LOCALAPPDATA%\ModernWigiDash; a
    copy next to the exe from older releases migrates automatically,
    one time. Profile exports carry the theme, and importing one offers
    a one-click  Restore Theme  (declining leaves your theme untouched).
  - Clock: optional seconds display (hh:mm:ss / HH:mm:ss).
  - Weather: optional Hide Location (the city name hides; the custom
    label and the guidance lines keep rendering).
  - Fixes: the global-hotkey startup crash (a P/Invoke binding miss),
    the audio-capture retry after a failed start, updater digest-case
    hardening, and the exit standby verdict line.


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
       "Intel PresentMon"         -> Uninstall
  2. (If you enabled Start with Windows) untick it in the settings hub,
     or delete the  ModernWigiDash  value under
     HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
  3. Delete the ModernWigiDash-win-x64 folder.

== License & credits ==

  ModernWigiDash            MIT  (LICENSE-ModernWigiDash.txt)
  LibreHardwareService      MPL-2.0  — https://github.com/epinter/LibreHardwareService
  PresentMon Service        MIT  — https://github.com/GameTechDev/PresentMon
  See  telemetry\third-party-licenses\NOTICES.txt  for full texts + sources.
