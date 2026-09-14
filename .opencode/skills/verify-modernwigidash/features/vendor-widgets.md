# Feature: Vendor widgets (HWiNFO Sensor, AIDA64 Panel)

The two widgets backed by G.SKILL's WigiDash service. HWiNFO Sensor reads any
sensor the vendor service exposes and draws it in the shared telemetry display
modes; AIDA64 Panel renders the AIDA64 sensor panel. Both degrade to a named
placeholder when the vendor software is absent, so the surface is drivable on any
host.

## Sub-features

- `place-hwinfo` — the catalog places an HWiNFO Sensor widget on the active page.
- `sensor-picker` — the inspector's Sensor dropdown lists the vendor's live catalog and switches the drawn value.
- `display-modes` — Gauge / Bar / Value / Graph on the HWiNFO widget.
- `place-aida` — the catalog places the AIDA64 Panel full-screen.
- `aida-master` — the app registers the vendor's slot 0 and renders AIDA64's frames (one master per process).
- `vendor-absent` — both widgets draw their named placeholder.

## How to get to it (user POV)

Add a widget from the catalog (Widget Catalog -> **HWiNFO Sensor** / **AIDA64
Panel** -> *+ Place on Canvas*). Select it on the canvas to open its inspector.
HWiNFO Sensor's **Sensor** row is a dropdown of the vendor's sensors; **Display
Mode** cycles Gauge / Bar / Value / Graph. The AIDA64 Panel lands full-screen
(1016x592).

## Driving it with wmd-verify

1. `launch`, then `doctor`.
2. `click "BtnPlace_hwinfo"`; `value ActiveCount` rises by one.
3. `click "BtnPlace_aida_panel"`; `value ActiveCount` rises again.
4. `click-screen <canvasPoint>` to select the HWiNFO widget; `dump` to read the
   inspector (Sensor / Display Mode / Auto Scale / Max Value / Decimals / Accent
   Color / Text Color).
5. `click-screen` on the Sensor combo's arrow, then on an item: the canvas value
   switches.
6. Repeat the combo clicks for **Display Mode** -> `Graph`; `shot`.
7. `clean` (the run mutates the profile).

## Gotchas

- The canvas has no UIA peer, so selection and the combo clicks go through
  `click-screen` with absolute coordinates (read them from `dump` first).
- Live data needs G.SKILL's WigiDash service; the AIDA64 panel's content needs
  AIDA64 running with LCD items configured. Absent, the placeholders are the
  expected proof.
- Two AIDA64 placements share ONE master: exactly one `[AIDA-MASTER] polling
  started` and one `registered slot 0` line per app run (the log is the second
  view for the sharing rule).
