# 2026-08-23 VS 2026 Copilot code review run

Drove Visual Studio Community 2026 (18.8.2) via UIA and ran Copilot code
review two ways: the built-in Git Changes review on the uncommitted working
set, and an agent-mode chat review of the last 8 commits (HEAD~8..HEAD, the
2026-08-23 tooling/gates/updater-hardening sweep, 40 files).

## Run 1: built-in review (uncommitted changes)

Entry point: Git Changes window, Changes section, Options button,
"Review changes with Copilot (might be inaccurate)". Scope: the 2 modified
files (`.audit/gates.tsv`, `Directory.Packages.props` Roslynator 4.16.1 to
5.0.0 bump).

Result after ~6 min: **"Copilot did not comment on any files."** No inline
diff annotations. Nothing to report on the working set.

## Run 2: agent chat review of HEAD~8..HEAD

Prompt (via right-click paste into the chat input; keyboard injection is
blocked in this agent session, mouse works):

> Review the code changes in the last 8 commits of this repository (from
> HEAD~8 to HEAD, the 2026-08-23 tooling, gates and updater-hardening
> sweep). Read the diffs with git, inspect the changed files, and report
> any bugs, security issues, or regressions you find, each with file and
> line. Do not modify any files.

The agent ran 18 tasks (git diff, file reads, a Copilot Terminal) and
reported three findings.

### Copilot finding 1: run-gates.ps1 `return` in ForEach-Object

Claim: `scripts/run-gates.ps1` lines ~116-120 use `return` inside a
`ForEach-Object` scriptblock to skip excluded prose files, which Copilot
said "can exit the whole script/function (or at minimum stop processing the
remainder of the pipeline)".

**Verdict: false positive (empirically disproven).** Test on this machine
(PowerShell 5.1):

```powershell
1..5 | ForEach-Object { if ($_ -eq 2) { return }; Write-Output "saw $_" }
# prints: saw 1, saw 3, saw 4, saw 5
```

`return` inside a ForEach-Object scriptblock exits only the current
scriptblock; the pipeline continues with the next input object. The prose
scan correctly skips excluded files and keeps going. Copilot's own text
hedged this ("if proven to only exit the scriptblock and continue
pipeline"). No change needed.

### Copilot finding 2: UpdateService.DigestMatches is case-sensitive

Claim: `ModernWigiDash.App/Update/UpdateService.cs` `DigestMatches`
(lines 55-59) compares the ASCII bytes of two hex strings case-sensitively,
so an expected digest in a different case than `ComputeSha256`'s output
fails even though the digests match; suggests converting to raw bytes with
`Convert.FromHexString` + `FixedTimeEquals`.

**Verdict: real but latent (robustness nit, low priority).** Verified
facts:

- `ComputeSha256` (line 219) always returns lowercase
  (`Convert.ToHexString(...).ToLowerInvariant()`).
- The zip's expected digest comes from GitHub's `assets[].digest` field
  (`UpdateChecker.FindDigest`, lines 83-107): `sha256:<hex>` with the hex
  taken verbatim, no case normalization. GitHub's own digest field is
  lowercase, so production today compares lowercase to lowercase and works.
- The staged-cmd digest (line 173) is app-internal: both sides are
  `ComputeSha256` output, so case can never diverge there.
- Telling inconsistency: `FindDigest` normalizes the `sha256:` prefix with
  `OrdinalIgnoreCase` (line 101) but the hex comparison itself is
  case-sensitive.

If a future digest source ever emits uppercase/mixed-case hex, a
legitimate update is rejected (the update flow fails closed, nothing
executes), so the cost is broken updates, not a security bypass. One-line
hardening option: compare with `StringComparison.OrdinalIgnoreCase` (both
values are hex-only), or decode both with `Convert.FromHexString` and
compare bytes (which also rejects non-hex input by throwing instead of
mismatching, so it needs a guard).

### Copilot finding 3: HttpClient.Timeout = 10s vs download semantics

Claim: `SharedHttp.Timeout` (line 24) is 10s and "in many runtimes" applies
to the whole request, so long downloads could time out unexpectedly.

**Verdict: false positive (documented design).** The XML doc on
`DownloadAndStageAsync` (lines 82-85) states exactly this: with
`HttpCompletionOption.ResponseHeadersRead` the 10s `HttpClient.Timeout`
expires at header arrival, and mid-body stalls are cut off separately by
the 15-minute `stallBound` CancellationSource (lines 96-98). The 10s bound
is intentional and the stall path is implemented. No change needed.

### Copilot's non-findings (correct as stated)

- `UpdateChecker.IsTrustedAssetUrl` restricting to `github.com` /
  `objects.githubusercontent.com` is the intentional trust boundary, not a
  bug.
- The new `prose` column in `.audit/gates.tsv` is handled by the updated
  `gate-guard.ps1`.

## Bottom line

- Working set: clean, no comments.
- 8-commit sweep: 3 reported findings; 2 disproven on inspection (one
  empirically, one documented-by-design), 1 a genuine latent robustness nit
  (case-sensitive digest comparison in the updater trust chain) worth a
  one-line hardening if desired.

## Fix applied (finding 2)

`UpdateService.DigestMatches` now decodes both digests to raw bytes with
`Convert.FromHexString` (case-insensitive by construction) and compares the
decoded bytes with `CryptographicOperations.FixedTimeEquals`. Non-hex or
odd-length input is a `FormatException`, caught and returned as a mismatch
(fail closed: the caller's mismatch path logs and deletes the download),
never a throw. The method is now `internal static` so the behavior is
pinned directly, matching the file's convention for pure rules
(`ComputeSha256`, `ExtractSlimZip`).

New tests in `UpdateServiceTests`:
- `DigestMatches_SameValueDifferentCase_ReturnsTrue` (upper vs lower, both directions)
- `DigestMatches_DifferentDigest_ReturnsFalse` (the reject property is unchanged)
- `DigestMatches_NonHexInput_ReturnsFalse` (non-hex, odd-length, length-mismatch)

Verification: build 0 warnings / 0 errors (under Roslynator 5.0.0), full
test suite 1685/1685 (1682 baseline + 3 new), `dotnet format
--verify-no-changes` clean.

## Notes on the run

- Keyboard injection (SendInput) is blocked in this agent session; all
  text input to VS went through clipboard + right-click Paste + Invoke
  click on Send.
- VS instance left running (dedicated, launched for this run) so the review
  thread and the Git Changes status strip can be inspected; stop it with
  `vs-uia.ps1 stop` or just close it.
- Driver used: `C:\Users\tobia\AppData\Local\Temp\opencode\vs-uia.ps1`
  (UIA v4 typed-vtable bridge, same approach as the wmd-verify harness).