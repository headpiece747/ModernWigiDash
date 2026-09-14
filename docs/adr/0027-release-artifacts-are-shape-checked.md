# ADR-0027: Release artifacts are shape-checked and the publish path never deletes a release

**Date:** 2026-09-14
**Status:** Accepted
**Deciders:** Project owner

## Context

Pushing the `v0.8.0` tag ran the `Release` workflow, whose publish step
found the release already present (it had been created by hand) and ran
`gh release delete $tag` followed by `gh release upload $tag ... --clobber`.
`gh release upload` cannot target a deleted release, so the step deleted
the release and then failed with `release not found`: the tag was left
with no release at all and the workflow went red. The same pair had
already appeared in the `v0.7.0` run on 2026-09-09, identical failure
line, so the path had been broken for at least two tags and the red runs
were never diagnosed.

Verifying that failure surfaced a second, quieter defect. `v0.7.0`'s
`win-x64.zip` asset is 105,873,732 bytes, within 1 KB of its own
`app-only.zip` (105,872,640). Both zips hold the same self-contained exe,
and the full bundle's extra ~218 MB is the bundled telemetry installers
(LibreHardwareService 57 MB + PresentMon 150 MB), so a real full bundle
lands near 3x the app-only zip (v0.6.10: 321/104 MB, v0.8.0: 327/110 MB).
Nothing anywhere compared the two artifacts, so a slim payload published
under the full-bundle name was indistinguishable from a release, and the
"fresh install" download carried none of the telemetry installers the
release README's quick start depends on.

The two defects share one root: the publish path was the only place a
release artifact's identity was decided, and it checked nothing. It
assumed delete-then-upload was idempotent (that cannot work), and it took
the artifacts on trust.

## Decision

The artifact and the publish each own a fact the other cannot check:

- **The publish path never deletes a release.** When the release exists,
  `gh release upload --clobber` replaces same-named assets in place, so
  re-running a tag repairs it instead of destroying it; only an absent
  release is created. A failed upload leaves the previous release and its
  assets intact, which is exactly what the old path did not.
- **The workflow verifies what it published.** After publishing, the
  release is read back and each asset's byte size must equal the file the
  run just built, both names present. This is the only check that sees the
  upload itself.
- **The build asserts the artifact shape.** `scripts/build-release.ps1`
  refuses a full bundle below 2x the app-only zip or below 150 MB, so the
  v0.7.0 pair fails the build instead of shipping. The ratio is the real
  invariant (the telemetry installers are the difference); the floor
  catches a degenerate publish that shrank both zips together.
- **A dev artifact cannot wear the release name.** `-SkipTelemetry` is the
  documented offline dev path and produces a full zip without the
  installers, so its output is named
  `ModernWigiDash-v<semver>-win-x64-dev-no-telemetry.zip` and it warns.
  The one guard that survives a hand upload is the filename.
- **The workflow owns a tag's release.** No hand-created, hand-edited, or
  hand-deleted release for a tag (`.opencode/AGENTS.md`, Releases bullet).
  Repairing an older tag means checking out that tag, copying the current
  workflow and build script onto a throwaway branch, and dispatching with
  `-f tag=<tag>`: `gh workflow run --ref <tag>` would run that tag's own
  older workflow file, which is the broken one.

## Consequences

- A bad artifact pair fails the release build instead of reaching the
  release page.
- Re-running a tag is a repair path. That is also the documented way to
  fix an older tag's assets.
- A hand-made release is now harmless (the existing-release branch
  replaces its assets) rather than destructive.
- The stale action majors were bumped in the same pass
  (`actions/checkout@v7`, `actions/setup-dotnet@v6`): the v4 majors target
  Node 20, which GitHub deprecated, and the resulting warning sat at the
  end of the failed run's log next to the real error.
- Scope: the shape check asserts size, not contents. A payload that is
  large but wrong passes it; the upload size check and the on-device pass
  (`hardware-e2e-validation`) cover that gap.
- Exit: retire the ratio check if the full bundle and the app-only zip
  ever stop differing by the telemetry set; retire the delete-free publish
  if `gh release upload` gains an atomic create-or-replace.

## Date

2026-09-14
