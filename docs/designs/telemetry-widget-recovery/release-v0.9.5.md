# Release v0.9.5

Status: released (v0.9.5, run 43147307869); post-release checks and the v0.9.4 to v0.9.5 end-to-end update passed. The tag carries this checklist with the result appended in a follow-up docs commit.

## Target

- Tag: annotated `v0.9.5` on the commit that contains this checklist.
- Version: `0.9.5`; the workflow derives it from the tag and
  `build-release.ps1` stamps `InformationalVersion` and `FileVersion`.
- `-Version` must equal the tag and match the `== What's new in v0.9.5 ==`
  heading in `release/README.txt`.
- Owner: `.github/workflows/release.yml` on the tag push. No release is created,
  edited, or deleted by hand.

## Artifacts

- `ModernWigiDash-v0.9.5-win-x64.zip`: the fresh-install bundle.
- `ModernWigiDash-v0.9.5-app-only.zip`: the updater payload (the updater accepts
  `ModernWigiDash-v*-app-only.zip` over https from GitHub and requires a
  `sha256:` digest on the asset).
- Telemetry versions are auto-resolved at build time; the shipped pair is read
  from the published full zip's `telemetry-versions.txt`.

## What changed

- The download path stages only; the restart prompt is the single launch site
  (`RunInstallPhase` is deleted, pinned by `DebtGuardTests`).
- `apply-update.cmd` validates the staged exe before the rename-aside, restores
  the previous exe on a failed copy, and takes an atomic swap lock. A second
  updater exits without starting a swap.
- `RunStartupPhase` clears a leftover swap lock at startup; `LaunchUpdater`
  never touches the lock. A manual app launch inside the swap window can clear a
  live lock (the startup phase cannot tell crashed from running without a clock
  or process inspection); the script's validate-before-move and
  restore-on-copyfail then leave a redundant swap, not a corrupt install.

## Procedure

1. The release commit contains the updater fix (code, script, tests, pin), the
   `release/README.txt` v0.9.5 section, the README update row, and the two
   design docs (`update-single-launch.md`, this checklist). `git status --short`
   must show only those; nothing else is staged.
2. Confirm the last `.audit/gates.tsv` row is green at the current HEAD, at most
   60 minutes old. If not, run `scripts\run-gates.ps1`.
3. Commit with an ASCII message file; push `master`.
4. `git tag -a v0.9.5 -m "ModernWigiDash v0.9.5"`. Before pushing, prove the
   target: `git rev-parse HEAD` equals `git rev-list -n1 v0.9.5`.
5. `git push origin v0.9.5`; watch the run
   (`gh run list --workflow "Release" --limit 1`, then
   `gh run watch <id> --exit-status`).
6. Run the post-release checks, then the end-to-end update, then record the
   result.

## Pre-flight evidence

- Gate: build 0 warnings, 3026 tests, format clean.
- `ps-hygiene`: 17 scripts pure ASCII, analyzer clean, 30 Pester tests.
- Local `build-release.ps1 -Version 0.9.5`: full 326.9 MiB, app-only 108.9 MiB
  (3.00x), sandbox extraction smoke PASSED. Telemetry resolved to
  LibreHardwareService 0.3.4 and PresentMon 2.6.0.
- Update tests: the two `DownloadAndStage_*` pins (zero spawns on success and
  failure), the embedded-script ordering pin, the launch-leaves-the-lock test,
  the startup lock cleanup, and the existing launch/refusal tests.
- `DebtGuardTests`: `.LaunchUpdater(` appears only in `MainWindow.Update.cs`;
  `RunInstallPhase` appears nowhere.

## Post-release checks

```
gh api repos/headpiece747/ModernWigiDash/releases/tags/v0.9.5 --jq "{draft, prerelease, author: .author.login, body_start: (.body[0:120]), assets: [.assets[] | {name, size, digest}]}"
```

Pass conditions:

- `draft` and `prerelease` false; `author.login` is `github-actions[bot]`.
- Exactly two assets, the full and app-only zips, each with a `sha256:` digest.
- Each `size` equals the run log's `verified <name>: N bytes` value with the
  thousands separators removed (`343,423,196` in the log and `343423196` from
  the API are the same number).
- The body begins with the ElmorLabs credit.
- `gh api repos/headpiece747/ModernWigiDash/releases/latest --jq .tag_name`
  returns `v0.9.5`.
- Download the full bundle, read
  `telemetry\third-party-licenses\telemetry-versions.txt`, and record both
  lines.

## End-to-end update (from the published v0.9.4 payload)

Preconditions:

- Close every running ModernWigiDash instance. The single-instance mutex is
  global to the session, so an extracted v0.9.4 would exit as a secondary.
- Back up `profile.json`, `app_theme.json`, and `calendar-credentials.bin` from
  `%LOCALAPPDATA%\ModernWigiDash`; restore them after the leg. The extracted
  install shares the same state directory as the real one.

Steps:

1. Download and extract the v0.9.4 app-only zip to a scratch directory outside
   the repo.
2. Launch it, wait for the update badge, and download v0.9.5.
3. Wait for the **early updater's timeout**, not a wall-clock guess: the script
   waits 60 one-second iterations, so `updates\update.log` gains
   `ERROR: app did not exit within 60s` at roughly 70-85 s. Wait for that line
   (or at least two minutes) before confirming the restart. This leaves the
   restart updater as the only one that swaps on this leg. Expect the ERROR
   line: it is the intended >60 s path, not a failure.
4. Confirm the swap and the relaunch with FileVersion 0.9.5, the v0.9.4 log
   line `[App] === Application exiting === reason=update-restart`, and the
   v0.9.5 line `[App] === Host === ... app=0.9.5`.
5. The single launch is exercised by the first update started by v0.9.5
   (v0.9.5 to v0.9.6). On a standard per-user install, `updates\update.log`
   then shows one `updater start args:` line per update restart instead of two.
   An elevated install logs the line again after re-exec, so count
   `swap complete` instead: one completed swap per update restart.
6. Recovery if an install directory ever has no exe but has
   `ModernWigiDash.App.exe.old`: rename the `.old` back. The new script also
   restores it on a failed copy.

## Rollback

Publishing precedes the size verification, so a bad asset can be live briefly.

- Gates or build fails: no release exists. Delete the remote and local tag, fix,
  re-tag.
- A published-asset pass condition fails (size, digest, author, body credit,
  `releases/latest`): do not delete the release and do not move the tag. Re-run
  the workflow on a branch built from the tag's tree plus the current
  workflow/build script, never master:
  `git fetch origin tag v0.9.5`, `git checkout -b release-v0.9.5-assets v0.9.5`,
  copy `.github/workflows/release.yml` and `scripts/build-release.ps1` from
  master, commit, push, then
  `gh workflow run Release --ref release-v0.9.5-assets -f tag=v0.9.5`.
  The `--clobber` upload replaces same-named assets and recomputes digests.
- If the shipped telemetry pair must be replaced: on the asset-repair branch,
  add `-LhsVersion <v> -PresentMonVersion <v>` to the build script call in the
  workflow, validate that pair with a local
  `build-release.ps1 -Version 0.9.5 -LhsVersion <v> -PresentMonVersion <v>`, then
  re-run per the asset-repair procedure. A usable but unexpected pair is
  recorded and shipped, not treated as a failed build.
- If the **end-to-end update** fails, the shipped code is wrong and a re-run
  cannot change it: do not delete the release and do not move the tag. Ship the
  fix as v0.9.6 and record the failure in the result.
- Never hand-attach, hand-replace, or hand-delete an asset or a release.
- No auto-revert: a released bad build is followed by v0.9.6. To install a
  release by hand, extract its app-only zip into the install folder's parent (or
  copy the zip's `ModernWigiDash-win-x64\*` over the install folder).

## Post-release result

The tag keeps `(pending)`; do not amend or move it. After the checks, fill this
section, run `scripts\run-gates.ps1` again (HEAD moved since the release commit,
so the commit guard's sha check needs a fresh row), then commit the doc and push
`master`.

- Run `43147307869`: success in 9m20s; gates, build, publish, size verify, and
  credit steps all green.
- Readback: tag `v0.9.5`, `draft=false`, `prerelease=false`,
  `author=github-actions[bot]`, body begins with the ElmorLabs credit;
  `releases/latest` returns `v0.9.5`.
- Assets:
  - `ModernWigiDash-v0.9.5-win-x64.zip`: 343,417,206 bytes,
    `sha256:20e59999cc901197bbece1f86c5e12b87e6471821b86339ac84db198c4cfda14`.
  - `ModernWigiDash-v0.9.5-app-only.zip`: 114,695,324 bytes,
    `sha256:7eae84acd682aac81647115d3716a84c00524c5872c18fde817f312bbcfb6a4c`.
  - The run log's verified lines match (343,417,206 and 114,695,324).
- Tag object `55153de8eb30387c12599ebd57169f833f0690d4` dereferences to
  `def2ffc`, the fix and checklist commit.
- Shipped telemetry, read from the downloaded full zip's
  `telemetry-versions.txt`: `LibreHardwareService=0.3.4`, `PresentMon=2.6.0`.
  It matches the run log's auto-resolved pair and the local pre-flight.
- End-to-end update from v0.9.4: app start 03:28:26; early updater 03:28:44,
  timed out 03:29:47; restart 03:30:00, `swap complete` 03:30:02; v0.9.5 start
  03:30:02 with `app=0.9.5`. v0.9.4 wrote
  `[SHELL] window closed (reason=update-restart, trayLive=True)` and
  `[App] === Application exiting === reason=update-restart`. Scratch install
  removed, profile restored.
- The single launch is not exercised by this leg (it ran v0.9.4's updater code).
  The one-versus-two `updater start args:` check is deferred to the first update
  started by v0.9.5, per the procedure.
