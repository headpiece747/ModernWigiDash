# Rules-file drift check, 2026-08-25

Skill: `rules-check-drift` (project adaptation: the rule-file set is
`.opencode/AGENTS.md` + `.opencode/rules/dotnet-rules.md` + `CONTEXT.md`;
`docs/adr/` checked as the ADR inventory the CONTEXT.md table points at).
Research and report only. No rules file was modified. The only file written
is this report.

## Step 0, deterministic pre-pass

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/ref-check.ps1`:

    ref-check: clean, 5 docs checked, no stale references.

Scope of the script (from its header): backtick-quoted path-like references
in `CONTEXT.md`, `.opencode/AGENTS.md`, `.opencode/rules/dotnet-rules.md`,
and `docs/agents/*.md` (5 docs). It resolves against the repo root, skips
URLs, home paths, and drive-letter paths (machine-local temp paths are
deliberate and user-specific), and does not check C# symbol names or
non-backtick references. Four of the findings below are outside that net
(symbol name, diagram box text, an inventory count, and two diagram rows the
script cannot see because they are not backtick-quoted).

## Range checked

Skill project adaptation: "use the last release tag as the diff range on a
clean tree: `v<last>..HEAD`". Last release tag is `v0.6.7`, so the range is
`v0.6.7..HEAD` = `v0.6.7..3d4a7cc`: 109 commits, 485 files, 25590 insertions,
4476 deletions.

Two baseline notes:

- The task stated baseline HEAD `8cf016c`, but the actual HEAD is `3d4a7cc`
  ("fix: bind the Twitch device-auth browser open behind a test seam"), one
  commit past the baseline. The check covered the true HEAD. That commit
  touches `TwitchSessionTests.cs`, `TwitchWidgetTests.cs`,
  `ModernWigiDash.Widgets/Twitch/TwitchAuthenticationService.cs`, and
  `CONTEXT.md` (test count 1796 to 1813, plus the TwitchSessionTests entry).
- Tracked tree is clean. Two untracked files exist under `docs/reports/`
  (`2026-08-25-health-check.md`, `2026-08-25-security-scan.md`), outputs of
  other runs; they do not affect the range.
- The last in-range drift-check commit is `24d30f8`
  ("docs: rules-check-drift over the v0.6.7 range"); roughly 41 commits
  postdate it. The gate trail (`.audit/gates.tsv`, read only) shows the
  latest row: `2026-08-25T16:26:06Z  8cf016c  twitch-open-browser-seam  ok 0 0
  ok 1813 0 ok ok`, a green four-stage run (build, test, format, prose) with
  1813 tests, recorded at the HEAD that existed when the gate ran; the row
  was committed as part of `3d4a7cc`. Per task constraint, no gate, build,
  or test was re-run.

## (a) Now-false rules

| Where | What is wrong | Evidence | Minimal fix |
|-------|---------------|----------|-------------|
| `.opencode/rules/dotnet-rules.md:33` | Cites the architecture pin as `ArchitectureTests.ProjectReferences_OnlyTheDocumentedLayeringEdges_Holds`; the method does not exist. | The actual test is `ProjectReferences_OnlyTheDocumentedLayeringEdges_Hold` (no trailing "s") at `ModernWigiDash.Tests/ArchitectureTests.cs:32`. It was introduced under that name by `91b5199` and never renamed; the docs line with the wrong spelling was written by `72cbd5d` (both in range). A `git log -S` for the "Holds" spelling hits only the docs commit. | Drop the "s": `..._Holds` to `..._Hold` on that one line. |

## (b) Drifted map entries

| Where | What is wrong | Evidence | Minimal fix |
|-------|---------------|----------|-------------|
| `CONTEXT.md:173`, Architecture Overview, App box | "MainWindow + partials (Context, ServiceIntegration)". `MainWindow.ServiceIntegration.cs` no longer exists. | The partial was deleted by `67ddf70` (an ancestor of `v0.6.7`, i.e. stale before this range; the service it served left with ADR-0005). Current partials are `ModernWigiDash.App/MainWindow.Context.cs` and `ModernWigiDash.App/MainWindow.Update.cs` (plus `MainWindow.xaml.cs`). The only other live-tree mention of `ServiceIntegration` is `CONTEXT.md:173` itself; the rest are dated records (`docs/adr/0004`, `docs/archive/`). | Change "(Context, ServiceIntegration)" to "(Context, Update)". |
| `CONTEXT.md:194`, Architecture Overview, Core box | "Compositor, Theming, Rendering, Telemetry, Models". Core has no Telemetry group. | Core directories are `Models`, `Plugins`, `Rendering`, `Resources`, `Theming` (same at `v0.6.7` and at `v0.4.1`). `ModernWigiDash.Core/Telemetry/` was deleted by `e901196` (2026-08-06, pre-range), which moved `FrameTimeStatistics.cs` to the Sdk root. The telemetry stores live in Sdk (`TelemetryStore.cs`, `TelemetryStoreFacade.cs`, `FrameTimeStatistics.cs`), which CONTEXT.md's own glossary rows already state ("TelemetryStoreFacade (Sdk)"). A `rg` for `Telemetry` across `ModernWigiDash.Core` finds zero files. | Replace "Telemetry" with "Plugins" (the real fourth group; the compositor sits in `Rendering/`, so the other four words keep meaning). |
| `.opencode/AGENTS.md:63`, Ported section, poteto-mode line | "ships 15 playbooks under `playbooks/` (investigation, bug-fix, perf-issue, hillclimb, runtime-forensics, trace-forensics, feature, refactoring, prototype, visual-parity, authoring-a-skill, eval, autonomous-run, session-pickup, pause-safely)". Only 13 playbook files exist; `runtime-forensics.md` and `refactoring.md` are absent. | `Get-ChildItem .opencode/skills/poteto-mode/playbooks` lists exactly 13 files. `git show 2fbcb5b --name-status` (the in-range commit that tracked the curated `.opencode` set, which also wrote this line) added exactly those 13 files; the two missing ones were never committed, and `git log --diff-filter=D` for the playbooks path is empty. Compounding: `poteto-mode/SKILL.md` lines 120 and 123 still route to `playbooks/runtime-forensics.md` and `playbooks/refactoring.md`, so an agent following the skill dead-ends on two routes. | In `.opencode/AGENTS.md:63`, change "15" to "13" and delete `runtime-forensics` and `refactoring` from the parenthetical list. (Alternative that keeps the line true instead: restore the two playbook files, but that is outside the four instruction surfaces; the `SKILL.md` routes would also need to be checked after such a restore.) |

## (c) New durable invariants from the range worth encoding

None. The in-range work that established standing rules (the four-stage gate
and commit guard, `91b5199`; the DebtGuard mechanical pins, `ebbdabf`; the
architecture pins, `91b5199`; the agent hygiene scanners, `604603a`; the
em-dash prose scope, `308293b`/`9cd5ef6`; the test-seam discipline for the
Twitch browser open, `3d4a7cc`) is already encoded in the four surfaces, and
the three prior in-range drift passes (`35fd28f`, `5f531b1`, `24d30f8`) added
what the earlier slices needed. Nothing in the last 41 commits creates an
unencoded standing rule.

## Checked, still true, no edit

Counts:

- "1813 unit tests" (`CONTEXT.md`, Testing section) equals the latest green
  gate row (1813 passed, 0 failed). The count was mid-range stale (1796 in
  the docs against 1812 in the gate rows) and was reconciled by `3d4a7cc`;
  it is currently true.
- "12 widget implementations": 12 `[WidgetMetadata]` declarations across 12
  files in `ModernWigiDash.Widgets`.
- Six projects: `ModernWigiDash.slnx` holds exactly the six documented
  projects (App, Core, Hardware, Sdk, Widgets, Tests).
- 17 ADRs: 17 files in `docs/adr/` (0001 to 0017) matching the 17-row table.
- Agent inventory: 9 agent files in `.opencode/agents/` matching the 9-row
  table (dotnet-architect, build-error-resolver, code-reviewer,
  performance-analyst, refactor-cleaner, security-auditor, test-engineer,
  poteto-agent, comment-sicko).
- Skill inventory: 53 skill directories = the 52 skills listed across the
  .NET Domain (4), Workflow (9), Meta (4), and Ported (poteto-mode + 21
  principle-* + 13 other) sections, plus `desloppify`, which the prose
  sections (Hygiene sweeps, Not Installed) reference. All 21 `principle-*`
  directories match the 21 named in the Ported section.

Architecture and seams (spot-verified against the tree):

- Layering edges: all 13 `ProjectReference` entries across the six csproj
  files are exactly the documented edges (Core to Sdk; Hardware to Sdk;
  Widgets to Sdk + Core; App to Core + Hardware + Widgets + Sdk; Tests to all
  five). Matches the CONTEXT.md reference-edges line and the
  `ArchitectureTests` pin the gate enforces.
- About 170 type names that CONTEXT.md states are modules or seams were
  confirmed declared in the src or Tests tree, including the ones the range
  moved or created: `WeatherFetchFlow`, `CaptureWindowGuard`,
  `WeatherResolutionState`, `PriceMapStore`, `RestPollLoop`,
  `TwitchChatConnection`, `MediaSessionMonitor`, `AudioCaptureLifecycle`,
  `IconPickerModel`, `DeviceAuthorizationModel`, `ThemeDraft`,
  `TrustedUriPolicy` (Sdk), `SetWidgetProperty` (default member on
  `IModernWigiDashContext`), `FramePump`, `StartupWiring`, `TeardownPlan`,
  `ShutdownOrchestrator`, `RepoScan`, `DebtGuardTests`, `ArchitectureTests`.
  Property-shaped seams named in CONTEXT.md exist as properties:
  `DisplayHidTransport.LibUsbDeviceProvider`, `DisplayHidTransport.CloseBudgets`.
- Inspector write-back: `InspectorController.ApplyPropertyValue`
  (`ModernWigiDash.App/Inspector/InspectorController.cs:181`) is still the
  single funnel, and it commits through the context's `SetWidgetProperty`
  (line 198), matching both the Inspector panel row and the
  SetProperty/PersistProperty row.
- All 150+ test classes/files named in the CONTEXT.md Testing section exist
  under `ModernWigiDash.Tests` (file list cross-checked in full).
- Constants stated in CONTEXT.md match the source: `FrameDelivery` default
  pacing 33ms; engine touch poll 16ms and reconnect 5s; `FileLog` cadence
  flush 8KB/250ms with `RotationCapBytes` shared by `CrashLog`; weather cache
  1MB; `PriceFeedManager.RestInterval` 30s; `DisplayGeometry` 1016x592x2
  (payload 1,202,944 bytes).
- `TwitchSession`'s test ctor carries the fourth seam parameter
  (`Action<Uri> openBrowser`) that `3d4a7cc` and the updated CONTEXT.md
  TwitchSessionTests entry describe; production binds
  `OpenAuthorizationPage`.
- Sdk one-type-per-file contracts: `IModernWidget.cs`,
  `IWidgetActionInvoker.cs`, `ModernWidgetBase.cs` each own a file;
  `Attributes.cs` bundles the attributes as `.editorconfig` documents.

Commands and tooling (existence and documented behavior):

- `scripts/run-gates.ps1`: four stages in order (dotnet build Release,
  dotnet test with the house temp BaseOutputPath, dotnet format
  --verify-no-changes, prose scan), stop-at-first-failure, appends one TSV
  row to `.audit/gates.tsv`; the prose stage's exclusions and the single
  exempt ADR-0009 line match the `.opencode/AGENTS.md` description exactly.
- `scripts/gate-guard.ps1`: blocks unless the last row is ok in
  build/test/format/prose, its sha equals current HEAD, and the run is under
  `-MaxAgeMinutes` (default 60); escape via `$env:WMD_GATE_GUARD_SKIP = '1'`;
  testable via `-GatesFile`. `scripts/hooks/pre-commit` is the thin sh shim
  over PowerShell, all as documented.
- `scripts/ref-check.ps1` ran clean (above). `scripts/build-release.ps1`
  exists (the dotnet-rules reference is by name only; it lives under
  `scripts/`, not the repo root). `docs/agents/issue-tracker.md` and
  `docs/agents/domain.md` exist. `verify-modernwigidash/scripts/wmd-verify.ps1`
  exposes every documented subcommand (launch, doctor, dump, find, list,
  click, click-nth, value, set, click-at, shot, wait, backup-profile,
  restore-profile, stop, clean, plus set-in and click-screen);
  `ablate-ai-layer/scripts/map_layer.py` exists.
- csproj-backed claims in dotnet-rules.md: App `<Version>0.0.0</Version>`;
  the memory-conservation runtime options (`System.GC.HighMemoryPercent`
  30, `System.GC.RegionSize` 1048576); the per-project global-usings table
  matches every `<Using>` entry in all six csproj files (Sdk: SkiaSharp;
  Core: SkiaSharp, Sdk; Hardware: Sdk; Widgets: SkiaSharp, Sdk,
  Core.Rendering, System.Globalization; App: Sdk, WPF trio, Core.Models;
  Tests: SkiaSharp, Sdk, App, Core.Models, Widgets, Time.Testing);
  `Directory.Packages.props` has central package management on.
- `.editorconfig` pins no `end_of_line` (ADR-0010 holds); MA0048
  (file-name-must-match-type) is severity none with the documented reason.
- `opencode.json` wires glider and glider-trace as the `.opencode/AGENTS.md`
  Tool Mapping section states; no RoslynNavigator anywhere.
- Not Installed claims hold: none of triage, prototype, wizard, teach,
  to-questionnaire, retro, or implement-spec exists under
  `.opencode/skills/`; the global skills dir has `tdd` and no `code-review`;
  there is no `hooks/` directory at the repo root.
- Machine-local references exist: `run-elev-no-uac.ps1` under
  `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\`, the
  `WmdElevatedRunner` scheduled task, `skillspector.exe` under
  `C:\Users\tobia\.local\bin\`, agnix 0.49.0 and ctxlint 1.1.3 (the ref-check
  header names the same ctxlint version).

## Observations (no rules-file edit proposed)

- `ModernWigiDash.Widgets/Twitch/TwitchAuthenticationService.cs` contains
  exactly one type, `TwitchSession` (line 3); the file name does not match
  the type name, and the string "TwitchAuthenticationService" never appears
  in the file's contents (the path dates to `c7474d1`, pre-range). Similarly,
  `ModernWigiDash.Hardware/Transport/WinUsbNative.cs` holds two types
  (`SetupApiNative`, `WinUsbBulkDevice`). These touch the dotnet-rules.md
  section 1 directive ("One type per file. File name matches the type
  name"), but the matching Roslynator diagnostic (MA0048) is disabled
  repo-wide in `.editorconfig`, so the tree does not conflict with an
  active gate. If the house wants directive and tree to agree, the options
  are to rename/split those two files or to amend the section 1 line; that
  is a code or rules decision, not drift found here.
- The two missing poteto playbooks also break `poteto-mode/SKILL.md` routes
  (lines 120 and 123). Restoring `runtime-forensics.md` and
  `refactoring.md` (or dropping those two SKILL.md routes) is the fix that
  addresses the skill side; the AGENTS.md count fix in section (b) addresses
  the instruction surface.