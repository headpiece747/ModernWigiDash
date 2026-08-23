# ModernWigiDash: OpenCode Configuration

This project uses a curated subset of [dotnet-claude-kit](https://github.com/codewithmukesh/dotnet-claude-kit)
for .NET development intelligence. Skills, agents, and rules are installed
under `.opencode/` and adapted for this project (WPF desktop app +
USB/Hardware + Widget plugin libraries + MSTest tests).

## Agent skills

### Issue tracker

Issues and specs live as GitHub Issues (`headpiece747/ModernWigiDash`); all operations go through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: `CONTEXT.md` at the repo root + ADRs in `docs/adr/`. See `docs/agents/domain.md`.

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

### Tool Mapping
The kit's RoslynNavigator MCP server is **not** installed. Glider (already wired
in `opencode.json`) provides the equivalent tools. Each skill and agent carries a
mapping note near its top (e.g. `find_dead_code` → `glider_find_unused_symbols`,
`get_diagnostics` → `glider_get_diagnostics`, `get_project_graph` →
`glider_get_project_graph`, `detect_antipatterns` → `glider_semantic_query`).
The `outdated` skill's `get_nuget_packages` has no Glider equivalent. Inventory
with `dotnet list package` and `Directory.Packages.props`.

### Meta Skills (from coleam00/skills, MIT)
- **rules-check-drift**: checks `.opencode/AGENTS.md` / `.opencode/rules/` / `CONTEXT.md` against recent changes; reports now-false rules and drifted map entries, minimal edit only. Run before every merge; use `v<last>..HEAD` as the range on a clean tree.
- **opportunity-scan**: scans agentmemory sessions (reactive: one run's artifacts; proactive: window of logs) and recommends what to encode next (rules/skill/hook/subagent/MCP). Outputs a self-contained HTML report in `docs/`.
- **ablate-ai-layer**: measures whether the always-loaded AI instructions still earn their place by running the same task with the layer intact vs stripped, in throwaway git worktrees. `scripts/map_layer.py` is adapted for `.opencode/`; `--runner` wraps `opencode run`.
- **second-brain-audit**: audits CONTEXT.md / AGENTS.md for state-shaped claims that stopped being true (memory rot), against the codebase + agentmemory. Phase 3 script skipped (no monetary values here); Phase 2 does the work.

### Ported (poteto pstack, MIT)

Installed under `.opencode/skills/`, trimmed for this repo's shape: single local
model (no per-role model routing, all subagents run on the session model), no
PRs/CI/Graphite (playbook closing step = the house build + affected-test gate +
conventional commit), no agent-transcripts source (reflect reads the session
digest). `deslop` → `desloppify`, control-skills → the verify pair, `interrogate`
→ `ocr_review` + `code-reviewer`, `why` → `docs/adr/` + `CONTEXT.md` + git history.

- **poteto-mode**: poteto's agent style, trimmed; ships 15 playbooks under `playbooks/` (investigation, bug-fix, perf-issue, hillclimb, runtime-forensics, trace-forensics, feature, refactoring, prototype, visual-parity, authoring-a-skill, eval, autonomous-run, session-pickup, pause-safely). Routes to the `poteto-agent` subagent.
- **principle-\*** (21): poteto's principles pack, one skill each: boundary-discipline, build-the-lever, encode-lessons-in-structure, exhaust-the-design-space, experience-first, fix-root-causes, foundational-thinking, guard-the-context-window, laziness-protocol, make-operations-idempotent, migrate-callers-then-delete-legacy-apis, minimize-reader-load, model-the-domain, never-block-on-the-human, outcome-oriented-execution, prove-it-works, redesign-from-first-principles, separate-before-serializing-shared-state, sequence-verifiable-units, subtract-before-you-add, type-system-discipline.
- **verify-modernwigidash**: drive the WPF app the way a user does and prove behavior with UIA evidence (`scripts/wmd-verify.ps1` harness: launch/doctor/dump/find/list/click/click-nth/value/set/click-at/shot/wait/profile backup+restore/stop/clean).
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
- **Handoff at the door.** Session end with work in flight → write the
  handoff doc (the `handoff` skill, renamed from `handoff-doc` upstream); its repo path is the record (put it
  where the work's durable state lives, CONTEXT.md, an ADR, or a repo file).
  The handoff doc, not the compacted session, is what survives a dead
  session.
- **Resume.** A fresh session opened with unfinished business ("resume",
  "where were we") → open the persisted handoff doc (or the repo record the
  work points at) and pick up from the state it records instead of
  re-deriving it.

## Not Installed (deliberately)

- `cwm-roslyn-navigator` MCP server: redundant with Glider
- bash hooks (`hooks/`), not Windows-native; the repo's build/test workflow is
  covered by the build-fix/verify skills and the temp-output test command
- Web/API/EF/Docker/Aspire skills (api-versioning, ef-core, ddd, clean-architecture,
  vertical-slice, docker, container-publish, aspire, serilog, opentelemetry,
  messaging, minimal-api, openapi, scalar, authentication), no such surface here
- Workflow skills that duplicate existing skills (plan, tdd, checkpoint,
  wrap-up, de-sloppify), the project already has a `desloppify` skill and
  the global `tdd` skill

## Verification Commands

- Build: `dotnet build ModernWigiDash.slnx -c Release --nologo`
- Tests (temp output avoids a running app instance locking the App output):
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- Format: `dotnet format ModernWigiDash.slnx --verify-no-changes --verbosity quiet`
  (line endings are deliberately unpinned, ADR-0010; do NOT re-add
  `end_of_line` to `.editorconfig`, it recreates a ~45,000-error wall on
  Windows checkouts)
- Full gate run (build → test → format, stops at first failure, appends one
  trail row per run to `.audit/gates.tsv`): `scripts\run-gates.ps1`, use it
  for full gate runs instead of the three commands above
- Live-stack run requires elevation. **User preference: no per-call UAC prompts**:
  use the no-consent runner:
  `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elev-no-uac.ps1 -Command "<cmd>"`
  (drops the command into `pending.ps1`, triggers the `WmdElevatedRunner` scheduled
  task, created once with `/RL HIGHEST`, so `schtasks /Run` needs no consent, and
  polls `result.txt`; the elevated token is held by the task, the command runs in the
  user session so UIA can drive the app). `run-elevated.ps1` (UAC per call) stays as
  the fallback. If the task is missing (e.g. Temp was cleaned), recreate it with ONE
  elevated call: `schtasks /Create /TN WmdElevatedRunner /SC ONCE /ST 03:13 /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\elev-runner.ps1" /RL HIGHEST /F`
