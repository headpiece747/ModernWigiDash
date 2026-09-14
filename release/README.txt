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
       Stock & Crypto, Picture & GIF, Weather, Calendar, Text  — all fine.
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

== What's new in v0.8.0 ==

  - New widgets from the vendor integration: HWiNFO Sensor (pick any sensor the
    vendor's WigiDash service exposes) and AIDA64 Panel (shows your AIDA64 sensor
    panel live on the display).
  - Both telemetry widgets (Hardware Monitor and HWiNFO Sensor) now draw in
    Gauge / Bar / Value / Graph modes, with text that always fits the widget.
  - The AIDA64 Panel is placed full-screen by default.
  - Removed the Spotify widget (Spotify's 2025 Web-API changes gate it behind a
    paid Premium subscription; the free Now Playing widget covers now-playing).
  - Reliability: the vendor connection recovers on its own when the service
    starts or restarts, vendor reads never block the UI, and a broken widget can
    no longer take the app down.


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

  Thanks to ElmorLabs, whose WigiDash service the HWiNFO Sensor and AIDA64
  Panel widgets read, and who helped with both integrations:
    https://github.com/ElmorLabs-WigiDash
