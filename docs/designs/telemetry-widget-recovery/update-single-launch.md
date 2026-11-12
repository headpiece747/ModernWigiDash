# Updater single-launch fix (v0.9.5)

Status: approved (implemented; release checklist is release-v0.9.5.md)

## Problem

`MainWindow.Update.DownloadUpdateAsync` called `UpdateService.RunInstallPhase`,
which downloaded and staged the payload **and launched the updater
immediately**. `ShowRestartPrompt` later called `LaunchUpdater` again, so two
`apply-update.cmd` processes existed whenever the user restarted inside the
early updater's 60 s wait.

The destructive ordering was in the script, not only in the second caller.
`apply-update.cmd` renamed the installed exe aside (`.old`) **first**, and only
then checked that the staged exe existed. A process that entered after the
winner had removed the stage would rename the new exe aside and abort at
`ERROR: staged exe missing`, leaving the install directory with no exe. Startup
recovery could not run because there was no exe to start.

Observed in the v0.9.3 to v0.9.4 end-to-end update: the early updater logged
`ERROR: app did not exit within 60s` when the restart took longer than a minute,
then the restart updater performed the swap. That run took the timeout branch
and never reached the swap; the no-exe sequence is read from the script's
ordering, not from that observation.

## Fix

1. `MainWindow.Update.DownloadUpdateAsync` calls
   `UpdateService.DownloadAndStageAsync` and reports its bool to the UI state.
2. Delete `UpdateService.RunInstallPhase`. It has no other production caller,
   and the composition it encodes is the defect: a download method must not
   launch.
3. `LaunchUpdater` remains the only launch site, called from
   `ShowRestartPrompt` after the user confirms. `DebtGuardTests` pins
   `.LaunchUpdater(` to that one file and `RunInstallPhase` to zero hits.
4. `apply-update.cmd` reorders the swap:
   - Take an atomic swap lock (`.update-running`, created with `mkdir`) after the
     write/elevation check. A second updater exits without starting a swap.
   - Validate the staged exe **before** moving the installed exe aside.
   - Restore `.old` over the exe when the copy fails after 10 tries.
   - Remove the lock on every exit path after it is taken.
5. `RunStartupPhase` clears any leftover swap lock at startup, so a crashed
   updater cannot block every future update. The claim is narrow: a manual app
   launch inside the swap window can clear a live lock, because the startup
   phase cannot tell a crashed updater's lock from a running one without a
   clock or process inspection. In that case the script's validate-before-move
   and restore-on-copyfail leave a redundant swap, not a corrupt install.
   `LaunchUpdater` never touches the lock.

The deleted method's invariant moves to `DownloadAndStageAsync`: staged means
launchable, and nothing else. The digest-failure path stays pinned by
`DownloadAndStage_FailedStage_DoesNotLaunch`; both tests force a digest mismatch,
so the catch's extraction/cleanup leg is not separately pinned.

## Staged payload lifetime (accepted behavior change)

The stage now lives from download until the confirmed restart.

- Closing the app any other way (tray quit, window close, session end, crash)
  does not install the update. Previously an early updater could swap on any
  exit inside its 60 s window; that side effect is gone on purpose.
- The next startup's `RunStartupPhase` deletes `staged/` and `downloads/`
  (`CleanupStale`), and the startup check re-offers the update. The recovery is
  a re-download.
- A `LaunchUpdater` false hides the badge for the session and leaves the stage;
  the next startup deletes it. The update is re-offered then.
- Keeping a stage across restarts was rejected: the updater cannot distinguish a
  deliberately kept payload from an interrupted swap, and the existing cleanup
  is what makes the interrupted-swap recovery trustworthy.

## Tests

- `DownloadAndStage_DoesNotLaunchTheUpdater`: a successful stage with a counting
  `startProcess` seam reports true with zero spawns.
- `DownloadAndStage_FailedStage_DoesNotLaunch`: a sha mismatch reports false and
  never reaches the launch seam.
- `ApplyUpdateCmd_ValidatesStageBeforeRenamingAndRestoresOnFailure`: reads the
  embedded script and asserts the staged-exe check precedes the rename-aside,
  and that `:copyfail` restores `.old`.
- `HouseRules_UpdaterLaunch_HasOneProductionCallSite`: `.LaunchUpdater(` appears
  only in `MainWindow.Update.cs`; `RunInstallPhase` appears nowhere.
- `RunStartupPhase_RemovesAStaleSwapLock`: a leftover `.update-running` is
  dropped at startup.
- `LaunchUpdater_LeavesTheSwapLockAlone`: a pre-created swap lock still exists
  after `LaunchUpdater` runs.
- The existing launch tests stay: a successful launch and the refusal paths
  (missing/tampered cmd, missing hash). The missing `{{RELAUNCH}}` marker still
  launches (the swap runs; the app does not relaunch): the test asserts the
  launch, but the warning line itself is not observed by a test.

## Targets

- `ModernWigiDash.App/MainWindow.Update.cs`: the download call.
- `ModernWigiDash.App/Update/UpdateService.cs`: delete `RunInstallPhase`; clear
  the swap lock in `RunStartupPhase`.
- `ModernWigiDash.App/Update/apply-update.cmd`: lock, validate-before-move,
  restore-on-copy-failure, lock removal.
- `ModernWigiDash.Tests/UpdateServiceTests.cs`: replace the two
  `RunInstallPhase_*` tests with the two download tests and the script pin.
- `ModernWigiDash.Tests/DebtGuardTests.cs`: the launch-site pin.
- `release/README.txt`: the v0.9.5 section.

The call-site change and the method deletion land in one commit; there is no
intermediate buildable phase.

## Release

- v0.9.5 ships this fix. The release checklist is
  `release-v0.9.5.md`.
- The v0.9.4 to v0.9.5 end-to-end update runs the **v0.9.4** code, so it still
  shows the old early launch. Wait for the early updater's timeout line
  (`ERROR: app did not exit within 60s` in `updates\update.log`, about 70-85 s)
  before confirming the restart, then verify the swap, the relaunch, and
  `reason=update-restart` (v0.9.4 carries the instrumentation).
- The single launch is observable in the **v0.9.5 to v0.9.6** update. On a
  per-user install, `updates\update.log` shows one `updater start args:` line
  per update restart with the fix and two without. An elevated install logs the
  line again after re-exec, so count `swap complete` (one per completed swap)
  instead.
- Recovery from the old double-updater's worst case: if an install directory
  ever has no exe but has `ModernWigiDash.App.exe.old`, rename the `.old` back
  to `ModernWigiDash.App.exe`; the script now also does this on a failed copy.
- If v0.9.5 is wrong: no state is retained (the stage is deleted at the next
  startup), and the next release is installed by extracting its app-only zip
  into the install folder's parent (or copying the zip's
  `ModernWigiDash-win-x64\*` over the install folder). There is no auto-revert;
  a rollback is a forward-fix.
