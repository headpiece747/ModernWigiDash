# ModernWigiDash: OpenCode Configuration

A .NET 10 / C# 14 WPF desktop app that drives a USB G.Skill WigiDash LCD with
customizable widgets. Six projects, dependency direction inward (Sdk leaf, App
top): Core -> Sdk; Hardware -> Sdk; Widgets -> Core + Sdk; App -> Core +
Hardware + Sdk + Widgets; Tests -> all five. The layering is machine-pinned by
`ArchitectureTests` against the csproj files. Read `CONTEXT.md` (domain glossary
+ architecture) before structural work; ADRs live in `docs/adr/`.

## Verification Commands

- Build: `dotnet build ModernWigiDash.slnx -c Release --nologo`
- Tests (temp output avoids a running app locking the App output):
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- Format: `dotnet format ModernWigiDash.slnx --verify-no-changes --verbosity quiet`
  Line endings are deliberately unpinned (ADR-0010). Do NOT re-add `end_of_line`
  to `.editorconfig`; it recreates a ~45,000-error wall on Windows checkouts.
- Full gate run (build -> test -> format, stops at first failure, appends one
  trail row to `.audit/gates.tsv`): `scripts\run-gates.ps1`. Use it for full
   runs instead of the three commands above. The build stage force-recompiles
   (`--no-incremental`): an mtime-stale incremental build can report UP-TO-DATE
   over changed content and hide a real warning. If the app is running from
   `bin\Release`, the forced recompile fails on a locked output file; stop the
   app first and re-run. On a test-stage failure the gate re-runs the suite at
   normal verbosity and folds the failing test names into the trail row's label
   (the quiet run prints only the summary count, never which tests failed, so a
   bare `FAIL` row would be undiagnosable: the 2026-09-08 one-off flake could
   not be identified because of exactly this gap). A failed gate is diagnosed
   from that row, never waved off as "a flake" by re-running until green.
- Harness ps1 lint (opt-in, NOT a gate stage, ADR-0010 precedent):
  `scripts\ps-hygiene.ps1` (pure-ASCII sweep + PSScriptAnalyzer over
  `scripts\psa-settings.psd1` + Pester over `scripts\tests\`). Run when a
  harness script changes or before a release.
- Coverage (regression floor, rerun after large test changes):
  `scripts\measure-coverage.ps1`. Baseline 2026-08-27: 87.9% of instrumented
  src lines (Sdk 92.9, Widgets 92.0, Hardware 89.5, Core 85.8, App 80.5).
- Commit guard: a pre-commit hook blocks a commit unless the last gate row in
  `.audit/gates.tsv` is green in all stages, its sha equals current HEAD, and
  the run is at most 60 min old. Install once per clone with
  `git config core.hooksPath scripts/hooks` (the hook `scripts/hooks/pre-commit`
  is committed; activation is local config). Logic lives in
   `scripts/gate-guard.ps1` (testable via `-GatesFile`); the hook then runs
   `scripts/scan-staged-cr.ps1`, which refuses a commit when a staged text file
   carries a lone CR (the git `text=auto` binary-classification trap), and then
   `scripts/scan-staged-userdata.ps1`, which refuses a commit that stages a
   personal/user-data file by basename (`profile.json`, `app_theme.json`,
   `app_settings.json`, `calendar-credentials.bin`, `media_artwork.bin`, and the
   `display_device.log*` family). The app's per-user state lives in
   `%LOCALAPPDATA%\ModernWigiDash` and is machine-local by design: `profile.json`
   carries the widget layout AND the ICS/CalDAV feed config, and the credential
   bin holds the DPAPI-wrapped CalDAV password, so none of it may reach the repo
   or GitHub. Basename matching catches a copy dropped anywhere in the tree;
    legit repo files (source, synthetic test fixtures, the per-instance weather
    cache) pass. Pinned by `scripts/tests/ScanStagedUserData.Tests.ps1`. Escape per
     invocation only: `$env:WMD_GATE_GUARD_SKIP = '1'` (skips the gate check; the
     CR + user-data scans still run). **Multi-commit trap (learned 2026-09-12):**
    the guard checks the gate row's sha against *current* HEAD, so the moment you
    land one commit, HEAD moves and the very next commit is blocked ("the tree
    moved after the gate") until you re-run `scripts\run-gates.ps1` to record a
    fresh green row at the new HEAD. When committing two or more logical changes
    in one session, re-run the gate between commits (or stage + commit them in a
    single pass), not just once up front.
- Commit messages from the agent shell: write the message to a temp file with
  `Set-Content -Encoding ascii` (or `utf8NoBOM`) and commit with
  `git commit -F <file>`. Do NOT use `-Encoding UTF8`: Windows PowerShell 5.1
  writes a BOM (U+FEFF) that lands in the stored subject, and do NOT pipe a
  here-string through `git commit -F -` (the agent shell can drop the body,
  leaving an empty commit). After committing, verify the subject is clean
  ASCII (`git log -1 --format=%s | ForEach-Object { [int][char]$_[0] }` must be
  < 128); a BOM-prefixed subject shows first-char code 65279/8745. The lone-CR
  guard covers line endings but not a leading BOM, so this check is the catch.
- Branch review: incoming PRs and feature branches go through the
  `code-reviewer` agent backed by `.opencode/rules/dotnet-rules.md`. The agent
  covers the judgment layer the pins cannot see: is an allow-list reason true,
  is an abstraction earning its place, does a Dispose path release every handle
  on every failure leg.
- Live-stack run requires elevation. **User preference: no per-call UAC
  prompts.** The no-consent runner (drops a command into a pending file,
  triggers the `WmdElevatedRunner` scheduled task created once with `/RL
  HIGHEST`, polls a result file) is owned by the `hardware-e2e-validation` and
  `verify-modernwigidash` skills; if the Temp-dir runner files or the scheduled
  task are missing (Temp was cleaned), recreate them per those skills rather
  than hand-rolling an elevated launch.

## Debt Guardrails (machine-pinned in `DebtGuardTests`, run in the gate's test
stage, so the commit guard enforces them before every commit)

Raw-scan pins against the src tree (shared mechanics in `RepoScan`):

- sync-over-async (`.Wait()` / `.Result` / `GetAwaiter().GetResult()`) only at
  the documented, budgeted sites (an allow-list with a reason per file,
  drift-checked both directions),
- `async void` only on event handlers (the `EventArgs` signature),
- every handle-acquiring file (P/Invoke extern, MemoryMappedFile, UsbContext,
  named mutex, loaded native library) carries its disposal evidence in the same
  file or a documented exception,
- the frame pipeline's encode + buffer pool have one entry (the
  `IRgb565Encoder.Encode` call and every `FrameBufferPool` reference sit in
  `FrameDelivery` + the pool's own file),
- no dead private helpers (a private method with no call site in its type's
  files or the project XAML, transitive chains included),
- every P/Invoke in src spells its `EntryPoint` explicitly (the method-name
  binding is a rename away from the first-call EntryPointNotFoundException; the
  spelled pairs are probed against the real DLL by `PInvokeBindingTests`,
  ADR-0020).

A violation fails the gate and the message spells the fix; a new legitimate
site is a deliberate allow-list edit with a reason. Hygiene sweeps:
`desloppify` is the periodic deep sweep for redundant abstractions; its
mechanical residue stays pinned in `DebtGuardTests` so it cannot regress
between sweeps. `unslop` is the prose pass, not a code sweep.

## Subagent Discipline (learned 2026-09-07)

NInfer runs with `--max-concurrency 1`: only one inference request at a time.
Launching multiple subagents in parallel floods the queue (HTTP 429 "queue is
full") and/or triggers client disconnects (HTTP 499) when the wait exceeds the
provider timeout. **Launch subagents one at a time, sequentially**: issue a
single `task` call, wait for its result, then issue the next. The provider
timeout (`"timeout": 600000` in `opencode.json`) prevents premature aborts
while a subagent waits in the queue, but it does not make parallel launches
safe. This applies to all specialist agents (code-reviewer, security-auditor,
test-engineer, etc.) and to OCR review runs.

## Security Rules (learned 2026-09-07 CalDAV audit)

These rules came from a real security audit that found two HIGH-severity
vulnerabilities in the CalDAV fetcher. They apply to ANY network-facing code
that sends user credentials or processes server-supplied URLs:

1. **Never send credentials over cleartext HTTP.** Any code path that attaches
   an auth header (Basic, Bearer, custom) must verify the resolved URI scheme
   is `https` before sending. A port-based scheme selection (`port == 80 ? http
   : https`) is NOT sufficient: the check must be on the actual `Uri.Scheme` of
   the request being sent, because redirects and server-controlled hrefs can
   change the effective scheme. The CalDAV fix: `ApplyBasicAuth` throws when
   `request.RequestUri.Scheme == "http"` and a non-empty credential is present.

2. **Server-supplied URLs must stay within the expected origin.** When parsing
   XML/JSON from a remote server and extracting a URL (href, link, redirect),
   verify the resolved URL's `Scheme` AND `Authority` match the base URI before
   fetching it with the user's auth header. A malicious server can return an
   href pointing at an arbitrary host, turning the client into an SSRF oracle
   and replaying credentials to a third party. The CalDAV fix: `MakeAbsolute`
   throws when `abs.Scheme != baseUri.Scheme || abs.Authority != baseUri.Authority`.

3. **Trim and validate user-supplied identifiers before embedding them in
   protocol headers.** A whitespace-padded username from a hand-edited profile
   produces a degenerate Basic-auth header. Trim at the fetch boundary and
   reject blank values with a clear error message.

4. **Decode HTML entities BEFORE stripping tags, and loop to a fixed point.**
   If you strip tags first and decode entities after, an encoded tag like
   `&lt;img src=x onerror=...&gt;` survives the strip (the regex sees no `<`)
   and the subsequent decode turns it into a live tag. Decode first, strip
   second, repeat until stable (max 3 passes). Use `System.Net.WebUtility.HtmlDecode`
   (handles all named + numeric entities) instead of a hand-rolled six-entity
   replace chain.

5. **Bare `catch {}` is never acceptable for untrusted-input parsers.** Catch
   `Exception` (not bare), exclude `OperationCanceledException`, and emit one
   log line naming the input source and exception type/message. A hostile feed
   that reliably triggered a parse fault must be observable, not silent.

## Session Errors (learned 2026-09-12, OCR calendar-review pass)

Real errors hit this session, each with the fix so they are not re-run into:

1. **Multi-commit gate block.** After landing commit N, HEAD moves and the
   commit guard blocks commit N+1 ("the tree moved after the gate") because the
   last green gate row's sha no longer matches HEAD. Fix: re-run
   `scripts\run-gates.ps1` between commits to record a fresh green row at the
   new HEAD (documented in the Commit guard bullet above). Cost this session:
   one blocked commit + a ~3 min gate re-run.
2. **Wrong enclosing class for a shared test const.** Referenced
   `CalendarWidgetTests.ValidIcsFeedJson` when the const actually lives in
   `CalendarWidgetTapToOpenTests` (a sibling class in the same file) -> CS0117.
   Fix: grep for the const's real declaring class before referencing it across
   files; two `[TestClass]`es can share one `.cs` file.
3. **Calling an expensive parse on the render tick.** First draft called
   `ParseFeeds()` (JSON parse + log) directly inside `Render` (30 FPS). Fix:
   cache the derived fact (`_feedCount`) as a field, refreshed in
   `RestartProducer` (the write-through path) and lazily on first render; the
   steady-state tick never re-parses. General rule: anything that parses JSON /
   logs / allocates must not run per-frame unless memoized.
4. **A behavior fix silently weakened existing tests.** The clobber fix made a
   feed-less widget stop reading the shared store, which broke
   `Render_DisplayMemo_HitsWithinAMinute...` (it seeded the store + used a
   no-feed widget) AND would have made several touch/scroll tests pass
   *trivially* (empty render -> nothing to tap). Fix: give those test widgets a
   real feed (`FeedsJson = ValidIcsFeedJson`) so they exercise the legitimate
   path. General rule: after a behavior change, check that previously-passing
   tests still assert what they claim, not just that they pass.
5. **Bulk `-replace` instead of the edit tool.** Fixed the class-name reference
   with a PowerShell bulk replace rather than targeted edits; it worked but was
   imprecise (formatting risk, no anchor verification). Fix: use the `edit` tool
   with a unique `oldString` anchor even for repeated substitutions; reserve
   bulk replace for whole-file mechanical rewrites you then re-read.
6. **Non-existent test seam in a first draft.** Drafted detail-mode tests
   calling `SetDetailEventForTest` (no such member) before checking the module's
   surface. Fix: read the target type's members (or its existing tests) before
   writing against them; prefer entering state through the production path (a
   hero tap) over inventing a seam.

## Tool Mapping

The kit's RoslynNavigator MCP server is NOT installed. Glider (wired in
`opencode.json`) provides the equivalent tools. Each skill and agent carries a
mapping note near its top (e.g. `find_dead_code` -> `glider_find_unused_symbols`,
`get_diagnostics` -> `glider_get_diagnostics`, `get_project_graph` ->
`glider_get_project_graph`, `detect_antipatterns` -> `glider_semantic_query`).
The `outdated` skill's `get_nuget_packages` has no Glider equivalent; inventory
with `dotnet list package` and `Directory.Packages.props`.

## Skills

Installed under `.opencode/skills/`. Load by name when the task matches.

### .NET Domain Skills
- **modern-csharp**: C# 14 language features for .NET 10 (primary constructors, collection expressions, `field` keyword, records, patterns, spans)
- **project-structure**: .slnx, Directory.Build.props, Directory.Packages.props (CPM), global usings, naming conventions
- **httpclient-factory**: IHttpClientFactory / typed clients / resilience / testing (for PriceFeedManager HTTP paths)
- **testing**: test strategy, AAA, naming, seam injection (NOTE: this repo uses MSTest, not xUnit)

### Workflow Skills
- **build-fix**: autonomous loops to drive a broken build or failing tests to green
- **verify**: 7-phase verification pipeline (build, diagnostics, antipatterns, tests, security, formatting, diff)
- **code-review**: multi-dimensional review with blast-radius prioritization
- **arch-check**: architecture conformance against the CONTEXT.md layering
- **health-check**: 8-dimension project assessment with letter grades
- **outdated**: dependency health report (staleness, vulnerabilities, license traps)
- **security-scan**: 6-layer security scanning (packages, secrets, OWASP patterns, auth, data protection)
- **convention-learner**: detect and enforce project conventions (writes to CONTEXT.md / AGENTS.md)
- **hardware-e2e-validation**: the physical-WigiDash loop: elevated launch, UIA driving, full update-cycle smoke; every on-device finding becomes a regression test

### Meta Skills (from coleam00/skills, MIT)
- **rules-check-drift**: checks `.opencode/AGENTS.md` / `.opencode/rules/` / `CONTEXT.md` against recent changes; reports now-false rules and drifted map entries, minimal edit only. Run before every merge; use the last release tag (e.g. `v0.6.8..HEAD`) as the range on a clean tree.
- **opportunity-scan**: scans agentmemory sessions (reactive: one run's artifacts; proactive: window of logs) and recommends what to encode next (rules/skill/hook/subagent/MCP). Outputs a self-contained HTML report in `docs/`.
- **ablate-ai-layer**: measures whether the always-loaded AI instructions still earn their place by running the same task with the layer intact vs stripped, in throwaway git worktrees. The skill's own `map_layer.py` (`.opencode/skills/ablate-ai-layer/scripts/`) is adapted for `.opencode/`; `--runner` wraps `opencode run`.
- **second-brain-audit**: audits CONTEXT.md / AGENTS.md for state-shaped claims that stopped being true (memory rot), against the codebase + agentmemory. Phase 3 script skipped (no monetary values here); Phase 2 does the work.

### Ported (poteto pstack, MIT)

Installed under `.opencode/skills/`, trimmed for this repo's shape: single local
model (no per-role model routing, all subagents run on the session model), no
PRs/CI/Graphite (playbook closing step = the house build + affected-test gate +
conventional commit), no agent-transcripts source (reflect reads the session
digest). `deslop` -> `desloppify`, control-skills -> the verify pair,
`interrogate` -> `ocr_review` + `code-reviewer`, `why` -> `docs/adr/` +
`CONTEXT.md` + git history.

- **poteto-mode**: poteto's agent style, trimmed; ships 15 playbooks under `playbooks/` (investigation, bug-fix, perf-issue, hillclimb, runtime-forensics, trace-forensics, feature, refactoring, prototype, visual-parity, authoring-a-skill, eval, autonomous-run, session-pickup, pause-safely). Routes to the `poteto-agent` subagent.
- **principle-\*** (21): poteto's principles pack, one skill each: boundary-discipline, build-the-lever, encode-lessons-in-structure, exhaust-the-design-space, experience-first, fix-root-causes, foundational-thinking, guard-the-context-window, laziness-protocol, make-operations-idempotent, migrate-callers-then-delete-legacy-apis, minimize-reader-load, model-the-domain, never-block-on-the-human, outcome-oriented-execution, prove-it-works, redesign-from-first-principles, separate-before-serializing-shared-state, sequence-verifiable-units, subtract-before-you-add, type-system-discipline.
- **verify-modernwigidash**: drive the WPF app the way a user does and prove behavior with UIA evidence (the skill's own `wmd-verify.ps1` harness, `.opencode/skills/verify-modernwigidash/scripts/`: launch/doctor/dump/find/list/click/click-nth/value/set/click-at/shot/wait/profile backup+restore/stop/clean).
- **maintain-verification-skill**: periodic pass keeping the verify skill and its feature map honest (parallel source readers, one live session, one small correction batch).
- **create-verification-skill**: generate a project-local UIA/CLI driving skill for a new repo.
- **how**: subsystem walkthroughs before changing something; placement/ownership/critique questions.
- **architect**: sketch types/signatures/module shape before code, then stay in the loop.
- **arena**: spawn N parallel candidates at the same task, graft the strongest parts (edge = fresh contexts, not model diversity).
- **reflect**: three parallel review subagents over the session; each learning routes to a concrete skill edit.
- **swarm**: fan out N parallel workers, drain, one report (parallel coverage, races, exploration).
- **no-comments**: spawn `comment-sicko`, fix accepted findings, offer encodings for claimed constraints.
- **show-me-your-work**: TSV decision log (what/why/evidence/result) for long or unattended runs.
- **figure-it-out**: auditable playbook for big migrations / multi-part work: hypothesis loop + decision log.
- **unslop**: cut AI tells from writing; always applied to prose outputs.
- **technical-writing**: Diátaxis + Google-style sentences + STE instructions for docs/RFCs/commits.

## Specialist Agents

Installed under `.opencode/agents/` (opencode subagents, `mode: subagent`).

| Agent | When to Use |
|-------|-------------|
| dotnet-architect | Architecture decisions, project structure, module boundaries, feature scaffolding |
| build-error-resolver | Autonomous build error fixing, iterative compilation repair |
| code-reviewer | Multi-dimensional code review, PR review, quality gatekeeper (read-only: edit denied) |
| performance-analyst | Profiling, frame-pipeline optimization, allocation reduction, async audit |
| refactor-cleaner | Dead code removal, systematic cleanup, safe refactoring with verification |
| security-auditor | Auth, secrets (DPAPI), USB vendor protocol, untrusted-import trust, vulnerability review |
| test-engineer | Test strategy, MSTest patterns, coverage of critical paths |
| poteto-agent | Routing target for /poteto-mode and any poteto-style request; reads the poteto-mode SKILL.md and applies the principle-* skills |
| comment-sicko | Deranged comment-hater: reports deletions and MUST KILL flags, never edits files (usually via no-comments) |

## Rules

Consolidated coding conventions in `.opencode/rules/dotnet-rules.md` (adapted
from the kit; web/API/EF rules removed, MSTest substituted for xUnit, and the
project's deliberate design decisions, synchronous transport per ADR-0001,
reflection-instantiated widgets with static stores, are preserved and flagged).

## Session Lifecycle

Session boundaries follow work state, not session length. Durable facts
persist at the moment they are created, so compaction costs nothing and a
session death is survivable.

- **Checkpoint.** The moment a durable fact exists (decision, ID, undo
  command, registration, file path), persist it to a repo file, or
  CONTEXT.md / an ADR, and to agentmemory when that MCP is connected. A
  fact that exists only in scrollback is a bug. Persist it before moving
  on. Secrets and PII stay out of memory and notes (dotnet-rules §3).
- **Switch point.** Announce a "good switch point" when the session is
  visibly long AND the current work block is verified-and-persisted (tests
  green, todos current, state in memory). One announcement per work block;
  the user restarts whenever convenient and nothing is lost either way.
- **Handoff at the door.** Session end with work in flight -> write the
  handoff doc (the `handoff` skill, renamed from `handoff-doc` upstream); its repo path is the record (put it
  where the work's durable state lives, CONTEXT.md, an ADR, or a repo file).
  The handoff doc, not the compacted session, is what survives a dead
  session.
- **Resume.** A fresh session opened with unfinished business ("resume",
  "where were we") -> open the persisted handoff doc (or the repo record the
  work points at) and pick up from the state it records instead of
  re-deriving it.

## Registry Candidates (intake filter, skills.sh pass 2026-08-24)

A skill proposed from skills.sh or any upstream registry must pass every check
below before install. A failed check is a dated rejection in the Not Installed
section, never a silent skip. Check 3's runtime half is ask-first, not an
automatic rejection: it is settled in-session, with the reason the runtime is
needed spelled out. The registry leaderboard is a lagging index for an adopted
upstream (renames ship upstream and the leaderboard lags; `two-axis-review` to
`code-review` is the recorded case), so a sync diffs against the upstream repo,
never the leaderboard.

1. **Shape**: local .NET desktop app. No web/TS, no database, no cloud, no
   media/video surface.
2. **Name**: unique across the project (`.opencode/skills/`) and the global
   (`~/.config/opencode/skills/`) locations, which opencode enforces.
3. **External runtime: ask first.** A tool, skill, or program that needs a new
   runtime (npm/Node, Python, `uv`, a global CLI, a new package manager) is
   allowed, but only after I flag it and explain exactly why that runtime is
   needed and what it pulls in (the 2026-08-27 decision replaced the outright
   ban, which had ruled out useful tools without a case-by-case look). The
   safety half stays hard and fails the check on its own: nothing inserted into
   the LLM provider traffic path, no telemetry, no cloud service.
4. **Distinctiveness**: no overlap with the existing catalog (judgment today;
   a mechanical scan once the catalog distinctiveness check lands).
5. **Hygiene**: `agnix` and `skillspector --no-llm` green after install.
6. **Claims**: a performance or quality claim is measured with a with/without
   run before the skill earns the catalog, or rejected on the claim alone.

### Accepted under the relaxed filter (2026-08-27 tooling pass)

- **agentmemory** (MCP, accepted 2026-08-27): the session-memory MCP the global
  `/remember` and `/recall` commands wrap. Canonical package
  `rohitg00/agentmemory`; wired project-scoped in `opencode.json` as
  `@agentmemory/mcp@0.9.29`, the standalone entrypoint (probes a full server at
  `localhost:3111`, falls back to a 7-tool local surface on file-backed storage
  at `~\.agentmemory\standalone.json`). Check-3 runtime: Node (already present,
  v24) + the npx-cached npm packages `@agentmemory/mcp` and the
  `@agentmemory/agentmemory` core (~6.3 MB). Safety half verified by dist
  inspection 2026-08-27: no daemon, no ports, no native binary on the
  standalone path (the pinned iii-engine v0.11.2 binary + 4 ports are server
  mode only, deliberately not used), no telemetry code on the standalone path,
  and no external endpoint without explicit provider config (keyless default:
  BM25 + local store). `memory_save` + `memory_smart_search` were verified by a
  stdio MCP round-trip probe before the entry landed. Dated final decision
  2026-08-27 (parked-resolution pass): the full 54-tool server (daemon +
  `iii.exe` + 4 ports) is REJECTED for this repo: the standalone 7-tool surface
  covers every workflow in use, the daemon + native binary is a larger attack
  surface than the file-backed store, and the safety inspection (dist walk)
  verified the standalone path only. Reopening is a deliberate act (start the
  daemon; the shim then proxies all 54 tools, which would also restore
  `/recall`'s lesson step), not drift.
- **cs4ai** (dotnet tool + skill, archived 2026-08-27): a semantic C# editor
  CLI surfaced from `~/.claude/skills`, off-catalog and never intake-reviewed;
  its "use INSTEAD OF Grep/Read/Edit" directive conflicts with the MCP-first
  house rule and its edit surface overlaps `glider_rename_symbol` /
  `glider_move_type` / `glider_move_member` and codegraph. The user does not run
  Claude Code, so the skill directory was archived to
  `~\.claude\skills-archived\cs4ai` (out of the skill scan path; restore is one
  move) and the `cs4ai` dotnet global tool stays installed. Reintake is a
  deliberate decision, not drift.
- **PSScriptAnalyzer + Pester** (PowerShellGet modules, adopted 2026-08-27, the
  parked-candidate resolution): the lint + test layer for the harness `.ps1`
  scripts (wmd-verify, run-gates, gate-guard, the elevated runner); their bug
  history (the here-string terminator that deleted `Ensure-WinMsg`, the PS 5.1
  Add-Type C#5 trap) was caught by hand each time until this. Check-3 runtime:
  none new (Windows PowerShell 5.1 + bundled PowerShellGet; Pester 5.7.1 needed
  `-SkipPublisherCheck` to shadow the bundled Microsoft-signed 3.4.0, both
  CurrentUser scope). Implementation: `scripts\ps-hygiene.ps1` (opt-in lint
  layer, NOT a gate stage, ADR-0010 precedent) runs three passes over
  `scripts\` + `.opencode\skills\`: the pure-ASCII byte sweep (PS 5.1 mis-parses
  non-ASCII), PSScriptAnalyzer with `scripts\psa-settings.psd1` (every exclusion
  a dated allow-list entry with its reason; `ExcludeRules` is the reliable
  disable key in the installed 1.25.0, the per-rule `Rules.Enable` form
  silently ignores five of the six probed rules), and Pester over
  `scripts\tests\` (GateGuard / RefCheck / ScanStagedCr / ParseRegression
  covering the commit-guard verdict surface, the stale-reference pre-pass, the
  staged-blob lone-CR scan against scratch repos, and the harness
  syntax-surface regression pins: zero parse errors, the top-level functions,
  the C#5-era Add-Type payloads compile, the here-string terminators at column
  0, and every empty catch documented with its preceding-line reason). First run
  fixed three real non-ASCII bugs (a BOM + two em dashes in `build-release.ps1`,
  an em dash in `measure-coverage.ps1`, a paint glyph in the `Get-AnyWindow`
  comment of `wmd-verify.ps1`) and the PS 5.1 comment-only-catch parse error the
  documentation comments almost introduced (17 parse errors before the fix; the
  reason comments now sit above the try).

## Not Installed (deliberately)

- markdownlint-cli (npm global, dated final rejection 2026-08-27, the
  parked-candidate resolution): prose lint for CONTEXT.md/ADRs. ADR-0010 is the
  cautionary tale (a mechanical prose gate made a ~45k-error wall), the prose
  surface is already governed by the `unslop` and `technical-writing` style
  rules, and an npm-global tool is disproportionate for what a style pass
  covers. Reintake is a deliberate decision (opt-in skill step only, never a
  gate stage), not drift.
- `retro` + `implement-spec` (mattpocock upstream, dated final rejection
  2026-08-27, the parked-candidate resolution): both still sit in upstream's
  `in-progress/` bucket at `6654f6b` (re-verified 2026-08-27, no newer
  upstream), and both overlap installed skills: `retro` (session retrospective +
  environment improvement suggestions) is `reflect` + `opportunity-scan`,
  `implement-spec` is `implement`. Reintake when they graduate upstream, not
  drift.
- `cwm-roslyn-navigator` MCP server: redundant with Glider
- bash workflow hooks (`hooks/`), not Windows-native; the repo's build/test
  workflow is covered by the build-fix/verify skills and the temp-output test
  command. (The gate-guard pre-commit hook under `scripts/hooks/` is a thin sh
  shim over PowerShell, git-hook shaped, not a bash workflow hook.)
- Web/API/EF/Docker/Aspire skills (api-versioning, ef-core, ddd,
  clean-architecture, vertical-slice, docker, container-publish, aspire,
  serilog, opentelemetry, messaging, minimal-api, openapi, scalar,
  authentication), no such surface here
- Workflow skills that duplicate existing skills (plan, tdd, checkpoint,
  wrap-up, de-sloppify), the project already has a `desloppify` skill and the
  global `tdd` skill
- Registry pass 2026-08-24 (skills.sh leaderboard, top ~400 + mattpocock
  upstream at `6654f6b`), each against the intake filter above: `triage` (a
  7-label issue state machine the to-spec to-tickets to implement flow does not
  use; the `/triage` flag reference in `docs/agents/issue-tracker.md` is
  annotated, not activated), `prototype` (duplicates the ported poteto
  prototype playbook; its UI branch is web-shaped: one route, a URL search
  param, a pnpm task runner), `wizard` (a bash + `.env` + WSL wizard; the
  human-only steps here are owned by the elevated runner and
  `hardware-e2e-validation`), `teach` (education workspace, no project need),
  `to-questionnaire` (async decision docs for another human; the solo project
  settles decisions in-session through the `question` tool and grilling),
  `retro` + `implement-spec` (upstream `in-progress` at `6654f6b`; superseded
  2026-08-27 by the dated final rejection in Not Installed), `obra/superpowers`
  (every salient skill overlaps an installed one: systematic-debugging =
  diagnosing-bugs, test-driven-development = tdd, verification-before-completion
  = principle-prove-it-works + verify, requesting/receiving-code-review = the
  code-review skill + code-reviewer agent, writing-plans/executing-plans =
  to-spec/implement, subagent-driven-development = implement + the task tool;
  2026-08-27: a stale `plugin` entry in the global `~/.config/opencode/opencode.json`
  referenced this repo, nothing was ever installed, and the entry was removed),
  `JuliusBrussee/caveman` (a product, not a skill: an npm CLI, a BSL-1.1 proxy
  in the provider traffic path, telemetry on by default; its pixel mode renders
  SKILL.md bodies to PNG, which agnix and skillspector cannot lint; the
  terseness idea is already unslop + technical-writing), `vercel-labs/find-skills`
  + `anthropics/skill-creator` (meta overlap: the intake filter above plus
  `authoring-a-skill` and `writing-for-agents`), and the leaderboard's dominant
  categories by shape (AI video/media, web UI design, databases, cloud, SaaS
  CLIs): no .NET/desktop surface on the top ~400
