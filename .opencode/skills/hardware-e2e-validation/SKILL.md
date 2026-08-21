---
name: hardware-e2e-validation
description: "The physical-WigiDash validation loop: run the app elevated on the real device, drive it via UIA, and verify the update cycle end-to-end. Use after release builds, before shipping hardware-touch features, or whenever a change touches the transport, the updater, or the launch/swap path. This loop caught 4 real bugs in the v0.5.0 updater that the 1025 unit tests could not — device behavior (job reaping, cmd quoting, batch self-delete, path joins) is only visible here."
---

# hardware-e2e-validation — the on-device loop unit tests can't see

The app streams to a physical USB display and runs an in-process updater. Unit
tests cover policy through seams; the device covers the rest — process
lifetimes, quoting, swap scripts, UAC. Everything in this skill is manual
because it must run elevated against real hardware.

## Prerequisites

- The release zip is built (`scripts\build-release.ps1 -Version X.Y.Z`).
- Operational helper scripts live in
`C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\` (machine-specific
   temp tooling — deliberately not in the repo): `run-elev-no-uac.ps1` (preferred
   elevated launcher — no UAC prompt; drives the `WmdElevatedRunner` scheduled
   task that holds the elevated token), `run-elevated.ps1` (UAC-per-call
   fallback), `uia-dump.ps1` / `win-dump.ps1` (UI tree), `upd-check.ps1`
  (button state), `upd-click.ps1` (invoke + poll), `upd-ok2.ps1` (confirm
  dialog), `upd-restart.ps1` (restart prompt), `smoke*.ps1` / `color-smoke.ps1`
  (frame smokes).

## The loop

1. **Fresh state** — delete `%LOCALAPPDATA%\ModernWigiDash\profile.json`
   (default profile regenerates) and any `updates\` stage.
2. **Elevated launch** — user preference: no per-call UAC waits. Use the
   no-consent runner (the `WmdElevatedRunner` scheduled task holds the elevated
   token, so this returns in seconds):
   `& C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elev-no-uac.ps1 -Command "<cmd>"`
   `run-elevated.ps1` (one UAC prompt per call) is the fallback if the task is
   missing — the recreation command is in `.opencode/AGENTS.md`.
3. **Stream check** — app log shows `Hardware connection successful!` and
   `BulkWrite` lines (frames are flowing).
4. **Update cycle** (when testing the updater) — `upd-check.ps1` for the button
   state, `upd-click.ps1` to invoke + poll to "Restart to apply",
   `upd-ok2.ps1` to confirm, `upd-restart.ps1` to relaunch; then verify the
   relaunched version, `%LOCALAPPDATA%\ModernWigiDash\updates\update.log`, and
   that profile + theme survived.
5. **The rule** — every on-device finding gets a **regression test through a
   seam** before the fix ships. The v0.5.0 updater's four device bugs
   (`Path.Combine`, `UseShellExecute`/`/c` quoting, run-outside-stage, stale-cmd
   cleanup) each became a unit test after the fact; do the same forward.
6. **Re-run the loop** after every fix until the full cycle passes twice in a
   row — one clean pass is a data point, not a verdict.

## Out of scope

- Pixel-level rendering QA (the widget smoke scripts cover this informally).
- Anything that can be asserted in unit tests — the loop is only for what the
  device uniquely proves.
