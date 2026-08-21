# Global Usings + Slim & Modern Program

> **For agentic workers:** This doc is both the handoff (decisions + measured
> facts from the 2026-08-21 planning session) and the execution plan. Start at
> **Task P0**. Track progress in the checkboxes; log decisions with the
> `show-me-your-work` skill (one row per choice). P0's output lives in a report
> file — do not accumulate it in chat.

**Goal:** Make the code and project as slim and as current (.NET 10 / C# 14) as
possible. House style rules (`.opencode/rules/dotnet-rules.md`, skills) are
guardrails against repeated mistakes, not a ceiling — the user explicitly
authorized going outside them when a better way exists. ADR + CONTEXT.md
invariants (ADR-0001 synchronous transport, ADR-0010 line endings,
reflection-instantiated widgets) are deliberate decisions: change only via ADR
revision.

**Source:** 2026-08-21 planning session. User mandate: *"I want the most updated
.NET 10 and C# standards, I want the code and project to be as slim and
efficient as possible. This project has been made with different harnesses and
LLMs so if something is the better way but outside of what the rules say try
it. Only use rules to help you not make the same mistakes over and over."*
Baseline: working tree at session start — no commits were made in the planning
session.

## User decisions (binding)

1. **Full program** (P0→P4 below), starting P0.
2. **Handoff written upfront** so P0's heavy output doesn't fill one context
   window — this doc + the decision log are the durable state.
3. **No `global.json`** SDK pin (explicitly declined 2026-08-21).
4. **Usings sweep = full sweep**: per-csproj `<Using>` items + file sweep +
   convention encoded.
5. **No new MCPs/plugins.** glider, glider-trace, codegraph, and ocr (verified
   healthy, v1.9.4) are sufficient; more servers = standing context cost.
   Rejected with rationale: CSharpier (conflicts with the
   `dotnet format --verify-no-changes` gate), StyleCop.Analyzers (overlaps the
   four analyzers already tuned in `.editorconfig`), BenchmarkDotNet (only if
   microbenchmarking is later required).

## Measured facts (2026-08-21; re-measure if the tree moved)

- 406 `.cs` files (excluding `obj/`/`bin/`), 1,217 explicit using directives
  (3.0 avg/file; 65% of files have ≤2).
- **48 restate the `ImplicitUsings` baseline** (`System.IO` ×31,
  `System.Net.Http` ×16, …) — pure dead weight, removable today.
- `using static` lines: 0. Alias usings (`using X = Y;`): **5 — a sweep must
  exact-match plain `using <Ns>;` lines only.**
- No `global using`, no `<Using>` items, no `GlobalUsings.cs` anywhere.
- `Directory.Build.props`: `ImplicitUsings enable`, `Nullable enable`,
  `AllowUnsafeBlocks`, `SupportedOSPlatformVersion`, `Company`; four analyzers
  (Sonar/Roslynator/Meziantou/AsyncFixer) already centralized. CPM in
  `Directory.Packages.props`.
- `.editorconfig`: no IDE0005 pin; the format gate checks whitespace/
  final-newline/charset only (ADR-0010 — do not re-add a line-ending pin).
- TFM per project: five projects `net10.0-windows10.0.19041.0`; **Sdk is plain
  `net10.0` deliberately** (cross-platform plugin contract) — do NOT hoist
  TFM.
- `ModernWigiDash.Hardware.csproj` L5 `<AssemblyName>` is redundant (SDK
  defaults to the project filename) → delete (approved as slimming).
- Package pins (freshness checked in P0): MSTest 4.3.3,
  Microsoft.NET.Test.Sdk 18.9.0, coverlet.collector 10.0.1,
  `Microsoft.Extensions.TimeProvider.Testing` 10.9.0, SkiaSharp, MessagePack,
  NAudio.Wasapi, LibUsbDotNet, `System.Security.Cryptography.ProtectedData`.
- App csproj load-bearing (do not touch): `<Version>0.0.0</Version>`
  (dev-build rule), `System.GC.HighMemoryPercent=30`/`RegionSize=1MB`,
  font/logo resources, updater resources.

### Per-project global-using candidates (≥5 files in the assembly)

| Project | files / directives | proposed `<Using>` items |
|---|---|---|
| Tests | 167 / 780 | `ModernWigiDash.Widgets` (85), `ModernWigiDash.Sdk` (45), `SkiaSharp` (43), `ModernWigiDash.App` (21), `ModernWigiDash.Core.Models` (19), `Microsoft.Extensions.Time.Testing` (19) |
| App | 75 / 193 | `ModernWigiDash.Sdk` (24), `System.Windows` (19), `System.Windows.Media` (12), `System.Windows.Controls` (10), `ModernWigiDash.Core.Models` (10) |
| Widgets | 103 / 171 | `SkiaSharp` (31), `ModernWigiDash.Sdk` (22), `ModernWigiDash.Core.Rendering` (14), `System.Globalization` (12) |
| Core | 16 / 34 | `ModernWigiDash.Sdk` (9), `SkiaSharp` (6) |
| Hardware | 16 / 23 | `ModernWigiDash.Sdk` (8) |
| Sdk | 29 / 16 | `SkiaSharp` (5) |

Expected reduction: ~600 lines across ~350 files + the 48 implicit-redundant.
Main CS0104 (ambiguity) risk: **Tests** (7 namespaces at once) — the
per-project build gate catches it.

## Global constraints

- **Verification commands** (from `.opencode/AGENTS.md`): build
  `dotnet build ModernWigiDash.slnx -c Release --nologo` · tests
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
  · format `dotnet format ModernWigiDash.slnx --verify-no-changes --verbosity quiet`.
- **Commits:** `type(scope): imperative`, one logical change per commit.
  Usings sweep = **one commit per project** (6), each independently green.
- **Glider:** csproj edits are structural → `glider_reload` after `<Using>`
  items land; the content-only sweep rides the watcher.
- **Never touch:** the 5 alias usings, ADR invariants (revision-only),
  line-ending discipline (ADR-0010), the App csproj items above.
- **No elevation needed** for this program (build/test/format only). If the
  live stack is ever required, use the no-consent runner per
  `.opencode/AGENTS.md` (user preference: no per-call UAC prompts).

## Task P0 — baseline evidence (read-only)

Output: `docs/reports/slim-baseline-<YYYYMMDD>.md` (house precedent:
`docs/reports/`).

- [x] `health-check` skill → 8-dimension letter grades. (A-, GPA 3.75; report:
  `docs/reports/slim-baseline-health-20260821.md`)
- [x] `outdated` skill → package freshness/vuln/license report. (All 16 CPM
  pins at latest stable; 0 CVEs; 0 license traps; report:
  `docs/reports/slim-baseline-packages-20260821.md`)
- [x] Glider dead code: all six checks run and re-verified in the main
  session. (0 deletable repo symbols — the 2 candidates are NuGet-package
  generated code; 42 unused params are all event-handler delegate signatures;
  0/13 unused project refs; 0 package version conflicts; 0 warning+
  diagnostics; top-15 complexity list matches the health report)
- [x] Per-`PackageReference` usage: all direct packages have live consumers
  (MessagePack, NAudio.Wasapi, ProtectedData, Logging.Abstractions,
  LibUsbDotNet, SkiaSharp, SkiaSharp.Views.WPF via XAML). Zero-usage candidates:
  none.
- [x] C# 14 compliance scan: 0 `Span`/`Memory` params (no `scoped` candidates
  — pipeline is array/pool-based by design), 4 collection-literal candidates,
  365 underscore fields (no bulk `field` conversion), primary-constructor
  gaps left to the P3 targeted pass.
- [x] Record findings + go/no-go per P2/P3 item.
  (Go/no-go recorded in `docs/reports/slim-baseline-20260821.md`.)

## Task P1 — global usings sweep (approved scope)

Order: **Sdk → Core → Hardware → Widgets → App → Tests.** Per project:

- [x] Add that project's `<Using>` items from the table to its csproj
  (nothing else; per-project TFM stays).
- [x] `glider_reload`; `dotnet build` — no CS0104 fired anywhere (the
  Tests six-way global set was pre-checked by the pre-sweep build).
- [x] Run the sweep script (`scripts/sweep-global-usings.ps1`, re-runnable;
  reads the global set from the csproj itself).
- [x] Delete the Hardware `<AssemblyName>` line (folded into its commit).
- [x] Full `dotnet test` (fresh artifacts — no `--no-build` with the temp
  BaseOutputPath) + `dotnet format --verify-no-changes` per project.
- [x] Commits: `3c11365` (Sdk, -5), `ec2fa6e` (Core, -15), `86650bd`
  (Hardware, -8), `e331ead` (Widgets, -79), `436b81a` (App, -75),
  `ece5992` (Tests, -232). Total: **414 using lines removed** (1217 → 803,
  avg/file 3.0 → 1.98).
- [x] Convention encoded: `.opencode/rules/dotnet-rules.md` §1 + the
  `project-structure` skill's decision-guide row corrected to the per-csproj
  reality.

**WPF rule (discovered in App, first sweep attempt failed the build):** the
WPF XAML markup pass compiles a generated `wpftmp` temp project that does not
reliably apply ImplicitUsings — stripping the SDK implicit-baseline usings
from a `UseWPF=true` project breaks the build (observed: CS0246 `HttpClient`
in UpdateService.cs). App and Tests therefore keep their explicit baseline
usings; the sweep script derives this rule from the csproj (`<UseWPF>`), so
no mode flag exists to get wrong.

## Task P2 — slim (from P0 evidence)

P0 evidence: no deletable repo symbols, no zero-usage package/project
references, no version drift. P2 is the single line-delete below.

- [x] Dead-code removal per P0 findings: **no action** (0 candidates).
- [x] Delete unused `PackageReference`/`ProjectReference` entries: **no
  action** (0 candidates).
- [x] Fix version drift: **no action** (0 conflicts).
- [ ] Delete the redundant `<AssemblyName>` line from
  `ModernWigiDash.Hardware.csproj` — **done in `86650bd`** (the 1621-test
  gate, which references the assembly by name, proved the output identity
  is unchanged).

## Task P3 — modern (from P0 evidence)

- [x] Package upgrades to latest stable: **no action** — all 16 CPM pins
  verified at latest stable 2026-08-21 (see the packages sub-report; do NOT
  "upgrade" NAudio.Wasapi to the unlisted 22.0.0).
- [x] C# 14 modernization per the P0 scan: 2 of the 4 collection literals
  converted (the two `var x = List<T> { … }` shapes were reverted — the
  .NET 10 Roslyn formatter mangles that shape, dropping the semicolon; the
  limitation is recorded in the .editorconfig note). `scoped` and bulk
  `field` conversions: no-op per P0 (documented rationale). Primary
  constructors: house rule already covers new code; the 4 existing
  primary-constructor sites show the codebase is at its natural adoption
  point — a bulk retrofit is churn, not slimming (recorded, not executed).
  Commit: `57597f0`.
- [x] `.editorconfig` curated style set at suggestion severity, verified
  against the format gate before committing (5 pins; the
  primary-constructor nudge deliberately excluded — it would wall the tree in
  suggestions). Commit: `f2b242a`.
- [x] BenchmarkDotNet: **no action** — no perf regression surfaced (the P0
  baseline has the measured memory footprint; nothing in this program touched
  hot paths).

## Task P4 — encode & drift check

- [x] Convention encoding: done inline during P1 (`dotnet-rules.md` §1
  usings rule + the `project-structure` skill correction, `a444839`) and P3
  (the 5 editorconfig style pins, `f2b242a`) — a separate convention-learner
  pass found nothing further to encode.
- [x] `rules-check-drift` over the full commit range (`50745c8..HEAD`):
  one factual drift found and fixed (CONTEXT.md test count 1614 → 1621);
  the rest of the rules set verified still true. Commit: `35fd28f`.
- [ ] Optional: `ablate-ai-layer` — **not run** (explicitly optional; the
  rules the program touched were re-verified instead of ablated). Open for a
  future session — see the "Next session" section below for the designed
  trap task and the now-satisfied gitignore prerequisite.
- [x] Final gate: `ocr_review` over the range **timed out at 3600 s** on the
  ~340-file mechanical diff (budget spent on using-removals the compiler
  already proved); a second, narrowly-scoped attempt (9 non-`.cs` files:
  the 6 csproj, `.editorconfig`, 2 ps1 levers — mechanical diff excluded)
  also timed out at 3600 s, with an invalid-JSON preview failure pointing
  at a tool-side range-mode bug — recorded as OCR-unavailable-for-range-mode
  per the one-attempt rule; the `code-reviewer` agent covered the same scope
  semantically — all 240 removal files checked against their project global
  sets, zero cross-namespace simple-type collisions, csproj `<Using>` sets
  exact, editorconfig pins suggestion-only with no line-ending pin, test
conversions proven equivalent, no scope creep. Its one MINOR (103 leading
   blank lines at file heads) was fixed in `690e1f2`. Build + full test suite
   (1621) + format gate green at the final state. Full report:
   `docs/reports/slim-final-review-20260821.md` (also carries the
   test-count reconciliation and the gate-evidence caveat).

## Persistence caveats (stated, not hidden)

- **`.opencode/` is partially gitignored** (policy changed 2026-08-21 on
  explicit user approval; commit recorded in the decision log): the curated
  set — `AGENTS.md`, `rules/`, `skills/`, `agents/`, `plugins/` — is now
  **tracked**, so the usings rule and skill corrections travel with the
  repo. `node_modules` (54 MB), `package*.json`, and the inner
  `.opencode/.gitignore` stay ignored; the root `.gitignore` carries a
  `/.opencode/**/node_modules/` backstop for fresh clones (the inner ignore
  file itself is untracked). History before that commit: the `.opencode`
  edits were local-only — the old caveat applies to pre-policy state only.
- **Sweep levers are committed** to `scripts/` (`sweep-global-usings.ps1`,
  `strip-leading-blank-lines.ps1`); the `Temp\opencode\` copies are the
  working originals and may vanish.
- **Per-project gate outputs were not committed** (house pattern: tests run
  into `Temp\opencode\wmd-build\`; commit messages record the result). The
  final-state gate is green at `690e1f2` and re-runnable.
- **Test-count story:** 1610 Roslyn-verified `[TestMethod]` methods; 4
  datarow-driven methods carry 15 rows; 1610 − 4 + 15 = **1621** executable
  cases (authoritative `--list-tests` count). The old `1614` was a stale
  historical figure. See the final-review report.

## Next session: ablate-ai-layer (prerequisite now met)

- [ ] `ablate-ai-layer` over the always-loaded layer (CONTEXT.md +
  `.opencode/AGENTS.md` + `.opencode/rules/dotnet-rules.md`). The gitignore
  prerequisite is satisfied as of the `.gitignore` policy commit (the
  curated set is tracked, so throwaway worktrees carry the layer).
- Primary task (designed trap): *"Promote `ModernWigiDash.Sdk` to a project
  global using in the App project and sweep the redundant usings."* The
  layer encodes exactly this lesson (the WPF temp-project rule, discovered
  the expensive way in P1). Interpretation: a stripped arm that fails the
  build with CS0246 in the wpftmp XAML pass proves the WPF rule
  load-bearing; a surviving arm is a trim candidate. Budget: standalone
  session (multiple agent runs per arm).

## Suggested skills (next session)

- **P0:** `health-check`, `outdated` (Skill tool) + Glider MCP directly.
- **P1:** `build-fix` if a gate goes red; `modern-csharp` as the C# 14
  reference.
- **P2:** `refactor-cleaner` agent; `desloppify` as optional second opinion.
- **P3:** `modern-csharp`; `performance-analyst` agent if hot-path work enters.
- **P4:** `convention-learner`, `rules-check-drift`, `ocr_review` +
  `code-reviewer`.
- **All:** `show-me-your-work` (decision log — in approved scope).
  `verify-modernwigidash` only if a user-facing seam moves (none expected).

## Resume

**Completed 2026-08-21** (all P0–P4 checkboxes closed except the optional
`ablate-ai-layer`, deliberately left open). The program's durable state:
`docs/reports/slim-baseline-*.md` (P0 evidence),
`docs/reports/slim-final-review-20260821.md` (final gate + reconciliation),
`.audit/slim-modern.tsv` (decision log), and the 12 commits
`126c22a..690e1f2`. A future session re-derives nothing from chat.