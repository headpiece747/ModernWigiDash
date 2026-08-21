# ModernWigiDash verification map

This directory is the maintained source for verifying the user-facing behavior of ModernWigiDash (the WPF app). Read the index before driving the app, then use the matching feature file as the recipe.

## Baseline preconditions

- The app is launched through the skill's harness (`launch`) and passes `doctor`. It runs unelevated in Simulated connection (no physical display attached by default).
- `backup-profile` ran before any feature that mutates the profile; the run's evidence dir exists at `%TEMP%\opencode\wmd-verify-evidence\<run-slug>\`.
- No second app instance exists (the harness enforces this).
- Never drive an instance that was not started by this verification run.

## Driving conventions

- Start every recipe from the baseline state unless its preconditions say otherwise.
- Prefer AutomationId/Name handles from the skill's handle table over coordinates; the canvas is the exception (`click-screen` with absolute screen pixels — the preview has no UIA peer).
- Treat every command as literal. Keep quoted names and flags unchanged.
- Run every action through `scripts/wmd-verify.ps1` (one invocation per action).
- Wait for observable results (re-read `value` until it changes), not fixed sleeps.
- Restore the seeded data after a mutation (`restore-profile` via `clean`). Do not remove proof artifacts during cleanup.

## Proof and skip reporting

- Capture the user action and the resulting state, not only the final screen.
- UI proof = command outputs + a `shot` PNG after the resulting state + a before/after `find`/`dump` read for structural claims.
- Mutation proof = a read-only second view of the stored value (profile JSON on disk, a re-read counter).
- Negative proof = the failed command or timed-out `wait`, with the unmet precondition named.
- Record the feature id and entry point used with every artifact.
- Report an unreachable path with the attempted commands and the unmet precondition.
- Do not report a skipped entry point as verified through a different path.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph describing the user-visible behavior. It then uses exactly four H2 sections in this order.

1. `Sub-features` lists short IDs with one line for each behavior.
2. `How to get to it (user POV)` lists every user entry point.
3. `Driving it with wmd-verify` starts with `Preconditions:` and uses labeled bullets that pair each user action with an exact command and observable result.
4. `Gotchas` lists traps that can waste or invalidate a verification run.

Keep implementation details out of the map. Name only user paths, stable handles, required state, commands, and observable proof.

## Features

- [Theme dialog](./theme.md) covers opening, canceling, applying, and resetting the chrome theme, with the app_theme.json persistence proof.
- [Pages](./pages.md) covers adding, switching, renaming, and deleting pages through the tab strip, with the tab-count and tab-name proofs.
- [Place a widget](./place-widget.md) covers placing from the catalog, the Active Widgets count proof, the canvas preview snapshot, and deleting through the inspector.
- [Profile round trip](./profile-roundtrip.md) covers export and import of the profile file, with the on-disk JSON second-view proof.
- [USB status badge](./usb-status.md) covers the badge's Simulated-mode state (UI side only; physical-display behavior routes to hardware-e2e-validation).