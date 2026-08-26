# USB status badge

The top-bar badge shows the engine's connection state as text: the app's single connection truth (Disconnected / Connecting / Connected / Simulated) mapped to a label and color. In this skill's unelevated, no-display environment, the badge settles to **Simulated** after the connect machinery gives up on finding the display.

## Sub-features

- `usb-simulated` badge reads `Simulated` on a machine without the display after the engine's connect cycle.
- `usb-label-set` the badge text moves only between the four state labels, never an ad hoc string.
- `usb-device` badge reads `Connected` with the physical display attached (out of scope here: routes to hardware-e2e-validation).

## How to get to it (user POV)

- Look at the badge in the top bar (a dot plus the state text `WigiDash` initially).

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor` and has been running long enough for the connect machinery to settle (the engine retries on a 5 s cadence in direct mode; allow one or two cycles after launch, or re-launch and wait).
- No display is attached by default; if one is attached, this feature's `usb-simulated` expectation flips to `Connected` and the physical path hands off to hardware-e2e-validation.

- **Read the state.** Run `value UsbStatus`. Expect `Simulated` (the mapping: Connected / Simulated / Connecting / Disconnected). Record it.
- **Pin the label set.** Run `dump | Select-String "UsbStatus"` once after launch and once after the settle: the text moved (or stayed) only within the four labels. Proof: the two outputs, saved as `<evidence>/usb/badgeset.txt`.
- **No phantom states.** The badge dot color is not UIA-readable (no peer; the Gotchas spell the pixel-sampling fallback), so the text is the assertion. Any text outside the four labels is a product regression, report it, do not work around it.
- **Device case (out of scope, documented).** With the display attached and the app run elevated per the hardware-e2e-validation skill, the same command expects `Connected`. This skill does not drive that case itself.

## Gotchas

- Right after launch the badge can read `Connecting` or the initial `WigiDash` label before the first connect verdict; read `value UsbStatus` again after one or two 5 s cycles before calling the state wrong.
- The badge dot (`UsbStatusDot`) and border (`UsbBadgeBorder`) expose NO UIA peer (WPF decorative elements); only the `TxtUsbStatus` text block is reachable. To assert the dot color, sample pixels at (textLeft-12, textTop+8±4): the 8 px dot sits 8 px left of the text, 12 px inside the border padding (verified 2026-08-26: a live `Connected` badge on the default theme samples exactly `#10B981`, the default `AccentGreen`).
- The `Connected` label text is amber (`M3Primary`) by design: green is the DOT's job. A stale `app_theme.json` next to the exe (the theme's real location, not LocalAppData) can re-color `AccentGreen` to a near-background value and make the dot invisible while the label still reads `Connected` in amber: check the exe-dir theme file before calling the badge wrong (see `theme.md`).
- Elevation changes the outcome: an elevated launch on a machine with the display attached can reach `Connected`, which invalidates this feature's Simulated expectation. The pre-condition (no display, unelevated) is part of the proof.
- The engine's standby/teardown only happens at app close; a stopped app is not a `Disconnected` state. The feature is about a live app, so prove it before any `stop`.
- The harness `stop` clean-closes first (WM_CLOSE through the app's own close path, which runs the standby handshake and releases the display pipe). Do not bypass it with a manual `Stop-Process` when you intend to launch again: a force-killed app wedges the display's bulk pipe, and the next launch's first connect fails its 1.2 MB init write, so the badge reads `Connecting`/`Disconnected` for up to 30 s until the LibUsb fallback reconnects.
- The badge text is the presenter's gate source of truth (the same value the touch-routing policy gates on); a wrong label here is load-bearing, not cosmetic.