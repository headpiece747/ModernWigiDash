# Release v0.9.4

Status: pre-flight complete, ready to tag

## Target

- Tag: annotated `v0.9.4` on the commit that contains this checklist.
- Version: `0.9.4`. The workflow derives it from the tag with the leading `v`
  stripped (`release.yml`), passes it as `build-release.ps1 -Version`, and the
  script stamps `InformationalVersion` and `FileVersion`. The csproj stays
  `0.0.0` so dev builds remain unversioned.
- `-Version` must equal the tag and match the `== What's new in v0.9.4 ==`
  heading in `release/README.txt`.
- Owner: `.github/workflows/release.yml` on the tag push. No release is created,
  edited, or deleted by hand.

## Artifacts

- `ModernWigiDash-v0.9.4-win-x64.zip`: the fresh-install bundle (full, with the
  bundled telemetry installers).
- `ModernWigiDash-v0.9.4-app-only.zip`: the in-app updater's payload. The
  updater accepts an asset named `ModernWigiDash-v*-app-only.zip` served over
  https from `github.com`/`objects.githubusercontent.com`, and requires the
  asset's `digest` to begin `sha256:`; otherwise no update is offered.
- The telemetry versions inside the full bundle are auto-resolved by
  `build-release.ps1` at build time (latest LibreHardwareService and PresentMon
  releases). The local pre-flight sizes are indicative; the shipped versions are
  the ones in `telemetry/third-party-licenses/telemetry-versions.txt` inside the
  published full zip.

## Procedure

1. Confirm only the intended changes are uncommitted (`git status --short`) and
   the last `.audit/gates.tsv` row is green at the current HEAD, at most 60
   minutes old. If not, run `scripts\run-gates.ps1`.
2. Commit with an ASCII message file:
   `Set-Content -Path <tmp> -Value <message> -Encoding ascii`;
   `git commit -F <tmp>`. The gate covers the parent commit; the workflow gates
   the tag commit itself.
3. `git push origin master`.
4. `git tag -a v0.9.4 -m "ModernWigiDash v0.9.4"`. Before pushing it, prove the
   target: `git rev-parse HEAD` must equal `git rev-list -n1 v0.9.4`. Then
   `git push origin v0.9.4`.
5. Watch the run: `gh run list --workflow "Release" --limit 1` to get the id,
   then `gh run watch <id> --exit-status`.
6. Run the post-release checks below before calling the release done.

## Release notes

- `release/README.txt` carries the v0.9.4 section: the AIDA64 freeze fix, the
  HWiNFO sensor picker, the faster truthful close, and the exit-reason lines.
- On the create path, the GitHub body is generated from the commits and the
  workflow prepends the ElmorLabs credit. On a re-run where the release already
  exists, the assets are replaced in place and the body is not regenerated; it
  keeps the existing body plus the credit.
- The shipped README gets exactly two version edits: the title substituted once
  (count 1, so the uninstall and `%LOCALAPPDATA%` paths stay unversioned) and a
  `== Version ==` block inserted before `== Quick Start (3 steps) ==`.

## Pre-flight evidence

- Gate at `6b94d2a`: build 0 warnings, 3021 tests, format clean.
- `ps-hygiene` locally: 17 scripts pure ASCII, analyzer clean, 30 Pester tests
  passed. On CI the step can exit green without the analyzer/Pester modules; the
  local run is the evidence for those passes.
- Local `build-release.ps1 -Version 0.9.4`: full 326.9 MB, app-only 108.9 MB
  (3.00x, above the 2x guardrail), sandbox extraction smoke PASSED.
- Packaged app-only zip: README has the v0.9.4 section and the `== Version ==`
  block, the uninstall and `%LOCALAPPDATA%` paths are intact, and the exe
  reports FileVersion 0.9.4.
- Device evidence in `design.md`: the freeze fix under a 10 s vendor hang, the
  live AIDA64 frame, the populated HWiNFO dropdown, and close in 60-87 ms.

## Post-release checks

Run the release readback:

```
gh api repos/headpiece747/ModernWigiDash/releases/tags/v0.9.4 --jq "{draft, prerelease, author: .author.login, assets: [.assets[] | {name, size, digest}]}"
```

Pass conditions:

- `draft` and `prerelease` are both false (a draft or prerelease is invisible to
  `releases/latest`, so no client would see it).
- `author.login` is `github-actions[bot]`.
- Exactly two assets, named `ModernWigiDash-v0.9.4-win-x64.zip` and
  `ModernWigiDash-v0.9.4-app-only.zip`.
- Each `size` equals the digit string of the most recent Release run's
  `verified <name>: N bytes` line with its thousands separators removed.
- Each `digest` begins `sha256:`.
- The body begins with the ElmorLabs credit.

Shipped telemetry versions (auto-resolved at CI time, so verify what actually
shipped): download the full bundle to a scratch directory with
`gh release download v0.9.4 --pattern ModernWigiDash-v0.9.4-win-x64.zip --dir <scratch>`,
expand it, read `telemetry\third-party-licenses\telemetry-versions.txt`, and
record both lines here. If the pair differs from the local pre-flight pair,
accept only after the size and update checks pass; otherwise treat it as a
failed build (see Rollback).

Tag target (annotated tag, so dereference it):

```
gh api repos/headpiece747/ModernWigiDash/git/ref/tags/v0.9.4 --jq .object.sha
gh api repos/headpiece747/ModernWigiDash/git/tags/<sha-from-the-first-command> --jq .object.sha
```

The second value must equal the checklist commit.

End-to-end update (only a release build can run this; a local dev build has
`AppVersion.Current == null` and never checks):

1. Download and extract the previous release outside the repo:
   `gh release download v0.9.3 --pattern ModernWigiDash-v0.9.3-app-only.zip --dir <scratch>`.
2. Launch the extracted v0.9.3 exe, wait for the update badge, download, submit
   the restart prompt, and confirm the log line
   `[App] === Application exiting === reason=update-restart`.
3. Confirm the relaunched exe reports FileVersion 0.9.4 and the app starts with
   the profile intact.

## Rollback

Publishing precedes the size verification, so a bad asset can be live briefly.
The policy:

- Gates or build step fails: no release exists. Delete the remote tag
  (`git push origin :v0.9.4`) **and** the local one (`git tag -d v0.9.4`), fix
  the tree, commit, re-tag at the new HEAD, and push.
- Any post-release pass condition fails after publish (size, digest, author,
  body credit): do not delete the release and do not move the tag. Re-run the
  workflow on a branch built from the tag's tree plus the current workflow and
  build script, never from `master`:
  1. `git fetch origin tag v0.9.4`
  2. `git checkout -b release-v0.9.4-assets v0.9.4`
  3. Copy `.github/workflows/release.yml` and `scripts/build-release.ps1` from
     `master` onto that branch, commit, and push the branch.
  4. `gh workflow run Release --ref release-v0.9.4-assets -f tag=v0.9.4`
  5. Confirm the run's build step checked out that branch and that the sizes in
     the run log match the readback.
  The `--clobber` upload replaces same-named assets and recomputes their
  digests; the credit step is idempotent. Nothing is uploaded or edited by hand.
- The workflow exposes no telemetry pins: if a shipped version pair must be
  replaced, add `-LhsVersion <v> -PresentMonVersion <v>` to the script call in
  the repair branch's workflow file, validate that pair with a local
  `build-release.ps1` run first, and use the same re-run procedure.
- A client that already installed a bad build cannot be recalled. Ship 0.9.5;
  do not roll the tag back after a release exists.
