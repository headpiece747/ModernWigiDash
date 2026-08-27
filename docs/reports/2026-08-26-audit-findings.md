# 2026-08-26 full-project audit: findings and fix status

Durable record of the 2026-08-26 audit pass and the fix pass that followed.
This file exists because the audit's claim list and its per-claim verdicts
lived only in the audit session's scrollback plus a temp-dir verification
script, which forced the fix pass to reconstruct the test-gap subset (E1-E6)
from the handoff. This file replaces that reconstruction with the verified
claim lists and their verdicts against the current source.

## Provenance

Two OCR passes exist for this audit window, and both are now cross-checked
below:

- **Raw 42-comment run** (the older, weather-centric pass; the handoff's
  "stale" JSON): `C:\Users\tobia\AppData\Local\Temp\opencode\ocr-review.json`
  (42 comments, fields `path/content/suggestion_code/existing_code/
  start_line/end_line/category/severity`). "Stale" means the claims are not
  facts: several were already fixed at audit time or were misread, so every
  claim was verified against the source before any action.
- **20-comment trusted pass** (the manager-parity + P/Invoke range
  `67c1549..1187636`, ADR-0019/0020): encoded as the re-runnable verification
  script `docs/reports/2026-08-26-audit-verify-claims.ps1` (a copy of the
  temp-dir `verify-ocr-claims.ps1` with the post-audit `DialogHost.cs` move
  applied). Each of its 26 sections is one claim plus the grep that proves
  or refutes it against the current source; the section count exceeds the
  comment count because a few comments are verified from two angles (code
  shape + test existence).
- **Fix verification**: per-fix targeted test batches plus the full house
  gate (`scripts/run-gates.ps1`), green at the close of this record.

## The E1-E6 test-gap list (recovered, not reconstructed)

The E1-E6 list was the audit's five "test existence" checks plus the
key-capture editor claim. Recovered from the trusted-pass sections 17 and
21-25:

| ID | Check | Status |
|----|-------|--------|
| E1 | `ChordKeyName` mapping untested | Closed by Fix #14 (`KeyCaptureEditorTests` pins the mapping, the `ResolvePressKey` policy, and the live-window glue) |
| E2 | zip-slip / `MaxUpdateBytes` untested | Already covered: `UpdateServiceTests` pins the zip-slip throw and the cap boundary |
| E3 | instance-id safety rule untested | Already covered: `ProfileImportSanitizerTests.IsSafeInstanceId_RejectsEveryUnsafeShape` (the raw comment named the rule "SafeCacheToken"; the real name is `IsSafeInstanceId`) |
| E4 | `WeatherPresentation.Build` untested | Already covered: `WeatherPresentationTests` pins the Build caps, the pass-through, and `BuildSubtitle` |
| E5 | `FrameDelivery` encode-failure recovery untested | Already covered: `FrameDeliveryTests.Push_WhenEncoderThrows_DropsAndSurvives`, rewritten to reuse the SAME delivery instance that dropped the frame |
| E6 | key-capture editor glue untested | Closed by Fix #4 (behavior) + Fix #14 (pins) |

## Trusted-pass claim status (26/26 resolved)

| # | Claim | Verdict | Closed by |
|---|-------|---------|-----------|
| 1 | `WeatherWidgetRenderer` paint disposal | Satisfied in source: the renderer is `IDisposable` (owns its paints, disposes them) and `WeatherForecastWidget.DisposeAsync` disposes the renderer | already in place at audit |
| 2 | `FrameDelivery._encodeFailLog` | Satisfied in source: non-nullable field, built from the injected log seam, writes on encode failure | already in place at audit |
| 3 | `IModernWidget` context declaration | Satisfied in source: the context rides the `InitializeAsync(context, ct)` parameter; the base stores it under the house `null!` pre-init shape with the contract spelled in the doc | already in place at audit |
| 4 | Sdk `InternalsVisibleTo` without rationale | Admitted: both grants load-bearing but undocumented | Fix #9 (rationale documented in `InternalsVisibleTo.cs`) |
| 5 | DialogHost color-picker width constant | Satisfied in source: the constant is `DeviceAuthWidth`, named for its only use (the file also moved from `Dialogs/` to the project root after the audit) | already in place at audit |
| 6 | `WidgetRouting` ZIndex tie-break | Satisfied in source: a tie goes to the LAST widget in list order, documented as matching the compositor's stable ascending-ZIndex paint order (the later-drawn widget is the visible one) | already in place at audit |
| 7 | `WeatherClient` outcome arms | Satisfied in source: the routing is the tie/non-tie pattern; the non-tie leg (unresolved or empty-candidate tie to `Failed`) is total by construction | already in place at audit |
| 8 | `ProfileOps` negated create pattern | Admitted: `created is not WidgetCreateResult.Ok ok` subtlety | Fix #8 (positive `Ok`/`Broken` patterns) |
| 9 | `WidgetPluginLoader` logs `ex.Message` only | Admitted | Fix #8 (`ex.ToString()` in the log line) |
| 10 | `UpdateService` zip-slip + cap | Satisfied in source: the zip-slip `InvalidDataException` guard + the 500 MB `MaxUpdateBytes` cap | already in place at audit |
| 11 | `HotkeyApi` entry points | Admitted: unspelled entry points (the 2026-08-26 `EntryPointNotFoundException` crash), a dead using, a missing `[return: MarshalAs]`, a dead `ModNoRepeat` const | Fix #1 (+ the ADR-0020 pin) |
| 12 | MainWindow ctor comment | Admitted: stale comment on the ctor-argument fallback | Fix #3 |
| 13 | MainWindow teardown + hotkey fields | Admitted: a post-teardown `RefreshGlobalHotkeys`/WM_HOTKEY could reach the disposed manager or a stale hwnd | Fix #3 (teardown nulls the manager, zeros the hwnd) |
| 14 | MainWindow duplicate-chord log | Admitted: duplicates logged per cell per refresh | Fix #3 (one line per conflict per session) |
| 15 | `LaunchAutoHotkeyScript` blank path | Admitted: a blank script path could reach the interpreter checks | Fix #10 (blank refusal + pin) |
| 16 | SettingsDialog AHK browse seam | Admitted: the browse seam returned nothing and the box kept its stale text | Fix #5 (`Func<string?>` seam) |
| 17 | key-capture editor behavior | Admitted: the focus-fail zombie arm, the missing SystemKey routing, Escape unhandled, numpad captures | Fix #4 (behavior) + Fix #14 (pins) |
| 18 | `TickerPresentation.FormatPrice` culture | Satisfied in source: routes through `DisplayFormat.Number` (the invariant culture contract) | already in place at audit |
| 19 | `WeatherLocationResolver` escaping/normalization | Satisfied in source: `Uri.EscapeDataString` on every URL field, `NormalizationForm.FormD` diacritic folding, the abbreviation tier | already in place at audit |
| 20 | `measure-coverage.ps1` report selection | Admitted: the stale `-p:CollectCoverage`, the unwiped BuildDir, the newest-report-without-provenance selection | Fix #11 |
| 21 | test existence: ChordKeyName | Admitted (gap E1) | Fix #14 |
| 22 | test existence: zip-slip / cap | Satisfied in source (gap E2 already covered) | already in place at audit |
| 23 | test existence: instance-id rule | Satisfied in source (gap E3 already covered) | already in place at audit |
| 24 | test existence: `WeatherPresentation.Build` | Satisfied in source (gap E4 already covered) | already in place at audit |
| 25 | test existence: FrameDelivery recovery | Satisfied in source (gap E5 already covered) | already in place at audit |
| 26 | `TwitchChatStatusPolicy` | Admitted: a repeated notice re-logged the failure and re-published the state; the login-failure notice set was incomplete | Fix #7 (the `Changed` predicate + the notice set) |

## Raw 42-comment cross-check

The raw JSON's claims, each verified against the current source. "Stale"
means the claim no longer holds against the source; "rejected" means it
holds but is a deliberate non-adoption.

| Raw # | Claim (abridged) | Verdict |
|-------|------------------|---------|
| 1 | `.gitignore`: `scorecard.png` not anchored | Fixed: the entry is now `/scorecard.png` |
| 2 | `.gitignore`: pytest cache dir spelled without the dot | Fixed: the entry is now `.pytest_cache/` (unanchored, matches the standard cache at any depth) |
| 3 | DialogHost: the width constant is named for a color picker it never serves | Fixed: the constant is `DeviceAuthWidth`, named for its only use |
| 4 | `.editorconfig`: the test-only section re-scopes everything after it | Fixed: an explicit `[*.cs]` re-open header with a comment follows the section |
| 5, 6 | `.editorconfig`: `*` does not match path separators in `ModernWigiDash.Tests/*.cs` | Accepted risk, documented: the `.editorconfig` note states all test files are top-level today and the section must follow if they move |
| 7 | `ThemeSettings`: `[StructLayout(Sequential)]` redundant on `RgbaColor` | Rejected: MA0008 (Meziantou) mandates the attribute on field-bearing structs; removing it fails the build (house precedent `WeatherLayout`) |
| 8 | `ProfileOps`: the instance-id guard has no test coverage | Covered by test: `ProfileImportSanitizerTests.IsSafeInstanceId_RejectsEveryUnsafeShape` |
| 9 | `ProfileOps`: the negated create pattern is subtle | Fixed by Fix #8 (positive patterns) |
| 10 | `WidgetPluginLoader`: the broken-widget path persists `ex.Message` only | Fixed by Fix #8 (`ex.ToString()` in the log line) |
| 11 | `UpdateService`: the zip-slip guard has no test | Covered by test: `UpdateServiceTests` pins the throw |
| 12 | `UpdateService`: the `MaxUpdateBytes` cap is untested | Covered by test: the boundary pins at the cap and cap+1 |
| 13 | `FrameDeliveryTests`: the recovery phase builds a fresh pipeline, proving nothing | Fixed by test rewrite: the SAME delivery that dropped the frame must deliver after the encoder recovers (the test comment spells the invariant) |
| 14 | `FrameDelivery`: `_encodeFailLog` nullable-annotated but unconditionally assigned | Fixed: the field is non-nullable |
| 15 | Sdk IVT friend declaration without rationale | Fixed by Fix #9 (rationale documented; both grants verified load-bearing) |
| 16 | `IModernWidget`: the doc says the context is null pre-init but the property is non-nullable | Resolved: the context rides the `InitializeAsync` parameter; `ModernWidgetBase.Context` is the house `null!` pre-init shape with the contract spelled in the doc |
| 17 | `TickerPresentation`: `DisplayFormat.Number` never truncates (behavior regression) | Stale: `DisplayFormat.Number` is `ToString("N" + decimals)` invariant, rounded to exactly the requested tier; the doc spells the upper-bound rule |
| 18 | `WidgetRouting`: the ZIndex tie-break is inconsistent with the stable paint order | Stale: the tie goes to the last widget in list order, documented as matching the compositor's stable ascending-ZIndex paint order |
| 19 | `DisplayFormat`: `Fps`/`FpsValue`/`Pct` let `PositiveInfinity` through | Fixed: every formatter guards with `double.IsFinite` |
| 20 | clamp tests check exact boundary values only | Fixed by test: 4, -1, 0, 5, 6, 30, 99, 100, 101, 500 |
| 21 | NOTICE tests pass bare keywords only | Fixed by test: a server-prefixed NOTICE (`:tmi.twitch.tv NOTICE #channel :...`) pin plus the repeated-notice no-change pin |
| 22 | `DisplayFormatTests` mutates the process-wide culture | Advisory: house convention (MSTest, no parallel execution); no change |
| 23 | `Changed` reports true when the status did not move | Fixed by Fix #7 (`Changed => status != current`) |
| 24 | `noticeText` dereferenced without a null guard | Fixed: `ArgumentNullException.ThrowIfNull` |
| 25 | the login-failure notice set is incomplete | Fixed by Fix #7 (all four notices: authentication failed, login unsuccessful, improperly formatted auth, invalid nick) |
| 26, 27 | `WeatherPresentation` mutates the shared cached `SKFont` (high) | Resolved by shape: `WeatherPresentation` no longer touches `SKFont` at all (pure display strings); the renderer owns its paints and disposes them; no cached-font mutation remains anywhere in Widgets |
| 28, 32 | the renderer holds `SKPaint` instances and never disposes them | Fixed: `WeatherWidgetRenderer : IDisposable`, the widget's `DisposeAsync` disposes it |
| 29 | `Build()` display logic has no test coverage | Covered by test: `WeatherPresentationTests` pins the Build caps and pass-through |
| 30 | the `WeatherClient` switch handles only `Resolved`/`Ambiguous` | Resolved: the routing is the two-way tie/non-tie pattern; the non-tie leg is total by construction |
| 31, 41 | the resolver extraction adds no dedicated test file | Covered by test: `WeatherLocationResolverTests` (the full battery) |
| 33 | the weather widget's instance-id guard has no test | Covered by test: the same `IsSafeInstanceId` pin as raw #8 |
| 34 | the coverage script picks the newest report without verifying it is this run's (high) | Fixed by Fix #11: the report is selected by `LastWriteTime -ge $runStart` under a fresh disposable results dir, failing loud on 0 or more than 1 report |
| 35 | `-p:CollectCoverage` is a no-op without `coverlet.msbuild` | Fixed by Fix #11: the property removed (the XPlat collector collects once attached, verified against this toolchain) |
| 36 | `$totalValid` division by zero yields NaN | Fixed: the total is guarded by `if ($totalValid -gt 0)` |
| 37 | the BuildDir is reused across runs without cleanup | Fixed by Fix #11: the disposable BuildDir is wiped before the run (the stale-incremental trap) |
| 38 | common jurisdiction abbreviations cannot match (the Contains tier skips short components) | Resolved: the first-class abbreviation tier (`StateAbbreviationMatches` + the response-aware weak-ISO fallback + the `CountryAliases` table) is the short-component route |
| 39 | `countryCode` interpolated into the URL unescaped | Fixed: `Uri.EscapeDataString` on every URL field (search + postal) |
| 40 | `Normalize(FormD)` throws on unpaired surrogates reachable from a hand-edited query | Fixed at this closeout: `NormalizeForMatch` degrades to the raw value instead of throwing; pinned by `WeatherLocationResolverTests.Resolve_UnpairedSurrogateInSuffix_DegradesWithoutThrowing` (the throw premise verified live against .NET 10) |
| 42 | every comparison re-normalizes both operands (perf) | Deliberately not adopted: the resolution path runs at fetch cadence (the 5-minute throttle), not frame rate; memoization would add mutable state to a pure static policy |

## Findings outside the OCR passes

The fix pass also carried findings from the audit's other review axes and
from the closeout work itself (not in either OCR list):

- **Fix #2** (`GlobalHotkeyManager`): owner-identity re-registration on a
  lost foreign-owned cell + the UI-thread doc.
- **Fix #6** (`AppSettingsStore`): catch widening, the null normalize, the
  tmp-file cleanup.
- **Fix #13** (dead-member trim): `PmStatus` reduced to the 8 codes the seam
  actually reads (explicit `PresentMonAPI.h` values); the write-only
  `TwitchTokenValidation.Login` removed (the DTO wire guard keeps its field).
- **Fix #15** (found by the coverage run, not by the audit): the
  `PriceFeedManager` first-claim startup and last-release teardown race over
  the same loop fields; the lifecycle gate makes them one serialized unit
  (the NRE surfaced when the coverage instrumentation widened the race
  window; the race test is green 5x and the price-feed cluster 101/101).

## Conclusion

- All 26 trusted-pass claims and all 42 raw comments are resolved against
  the current source: 0 open. The E1-E6 gap list is recovered (not
  reconstructed): one real gap (E1/E6, closed by Fixes #4 + #14), four
  already covered by tests in the repo.
- The earlier "not recoverable from disk" note is retired: the claim lists
  were recoverable from the temp-dir verification script and the raw JSON,
  and both are now pinned in this repo as this record plus the re-runnable
  `docs/reports/2026-08-26-audit-verify-claims.ps1`. The raw JSON
  (`ocr-review.json`) may still be wiped by temp cleanup; its stale claims
  are subsumed by this table, which stands on source verification, not on
  the raw comments.
- Closeout work: the unpaired-surrogate guard in
  `WeatherLocationResolver.NormalizeForMatch` (+ its test pin), the
  `PriceFeedManager` lifecycle gate (Fix #15), the `KeyCaptureEditorTests`
  glue pins (Fix #14), and the coverage-baseline refresh in CONTEXT.md.