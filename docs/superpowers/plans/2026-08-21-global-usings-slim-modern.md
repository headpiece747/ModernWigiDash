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
- [x] Run the sweep script (`C:\Users\tobia\AppData\Local\Temp\opencode\wmd-sweep-usings.ps1`,
  re-runnable; reads the global set from the csproj itself).
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

- [ ] Dead-code removal per P0 findings: **no action** (0 candidates).
- [ ] Delete unused `PackageReference`/`ProjectReference` entries: **no
  action** (0 candidates).
- [ ] Fix version drift: **no action** (0 conflicts).
- [ ] Delete the redundant `<AssemblyName>` line from
  `ModernWigiDash.Hardware.csproj` — **done in `86650bd`** (the 1621-test
  gate, which references the assembly by name, proved the output identity
  is unchanged).

## Task P3 — modern (from P0 evidence)

- [ ] Package upgrades to latest stable: **no action** — all 16 CPM pins
  verified at latest stable 2026-08-21 (see the packages sub-report; do NOT
  "upgrade" NAudio.Wasapi to the unlisted 22.0.0).
- [ ] C# 14 modernization per the P0 scan (primary constructors, `field`,
  collection expressions, `scoped` on hot paths) — separate verifiable
  commits, tests green each.
- [ ] Extend `.editorconfig` with a curated `csharp_style_*`/`dotnet_style_*`
  set at **suggestion** severity (house pattern: style = nudge, not gate).
  **Validate on a scratch branch first** that `dotnet format
  --verify-no-changes` is unaffected; revert if it fights the gate.
- [ ] (Only if a perf regression surfaces) BenchmarkDotNet on the RGB565
  encode path.

## Task P4 — encode & drift check

- [ ] `convention-learner` over what P1–P3 established → update
  `dotnet-rules.md`/`CONTEXT.md` where facts moved.
- [ ] `rules-check-drift` over the full commit range (AGENTS.md + CONTEXT.md +
  rules stay true).
- [ ] Optional: `ablate-ai-layer` — measure whether the always-loaded rules
  still earn their place (the user's "rules = don't-repeat-mistakes" thesis,
  tested).
- [ ] Final gate: `ocr_review` + `code-reviewer` agent over the program's
  whole diff; build + full tests + format.

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

Fresh session: open this doc, run P0, write the report file, then P1 with the
table above. All durable state after P0 lives in
`docs/reports/slim-baseline-*.md` + the decision log — nothing re-derived from
chat.