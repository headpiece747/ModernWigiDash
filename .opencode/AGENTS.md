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

Verified tooling quirks (2026-08-25 sweep session; each one hit live): the
`glob` tool never matches files under dot-directories (`.opencode/**` returns
nothing even from a root `**/` pattern; use `Get-ChildItem` or `grep` with an
explicit path there). `glider_get_structure` lists no members without
`includeMembers=true` (a `kinds` filter alone returns the container with zero
members). `ocr_review` in workspace mode selects only tracked diffs; an
all-untracked changeset reports "no items selected", so commit first or pass
an explicit `from`/`to` range. A solution-wide `-p:BaseIntermediateOutputPath`
breaks multi-TFM builds (NETSDK1005: one project.assets.json per TFM, the
last restore wins); use a temp `-p:BaseOutputPath` for temp output instead.
git 2.55 with `text=auto` classifies a file containing a lone CR (a CR not
followed by LF) as binary (`i/-text w/-text`), so `git add` stores raw bytes
and the diff becomes a whole-file EOL change: before committing, scan the
modified files for a stray 0x0D not followed by 0x0A. The PowerShell tool
transport strips backticks and mangles `$` variables inside inline commands;
for multi-step byte/regex work, write a `.ps1` to the temp dir and run it
with `-File`.

### Meta Skills (from coleam00/skills, MIT)
- **rules-check-drift**: checks `.opencode/AGENTS.md` / `.opencode/rules/` / `CONTEXT.md` against recent changes; reports now-false rules and drifted map entries, minimal edit only. Run before every merge; use `v<last>..HEAD` as the range on a clean tree.
- **opportunity-scan**: scans agentmemory sessions (reactive: one run's artifacts; proactive: window of logs) and recommends what to encode next (rules/skill/hook/subagent/MCP). Outputs a self-contained HTML report in `docs/`.
- **ablate-ai-layer**: measures whether the always-loaded AI instructions still earn their place by running the same task with the layer intact vs stripped, in throwaway git worktrees. The skill's own `map_layer.py` (`.opencode/skills/ablate-ai-layer/scripts/`) is adapted for `.opencode/`; `--runner` wraps `opencode run`.
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
- **Handoff at the door.** Session end with work in flight → write the
  handoff doc (the `handoff` skill, renamed from `handoff-doc` upstream); its repo path is the record (put it
  where the work's durable state lives, CONTEXT.md, an ADR, or a repo file).
  The handoff doc, not the compacted session, is what survives a dead
  session.
- **Resume.** A fresh session opened with unfinished business ("resume",
  "where were we") → open the persisted handoff doc (or the repo record the
  work points at) and pick up from the state it records instead of
  re-deriving it.

## Registry candidates (intake filter, skills.sh pass 2026-08-24)

A skill proposed from skills.sh or any upstream registry must pass every
check below before install. A failed check is a dated rejection in the
Not Installed section, never a silent skip. The registry leaderboard is a
lagging index for an adopted upstream (renames ship upstream and the
leaderboard lags; `two-axis-review` to `code-review` is the recorded case),
so a sync diffs against the upstream repo, never the leaderboard.

1. **Shape**: local .NET desktop app. No web/TS, no database, no cloud, no
   media/video surface.
2. **Name**: unique across the project (`.opencode/skills/`) and the global
   (`~/.config/opencode/skills/`) locations, which opencode enforces.
3. **No new external runtime**: no npm/Python/global-CLI install, nothing
   inserted into the LLM provider traffic path, no telemetry, no cloud
   service.
4. **Distinctiveness**: no overlap with the existing catalog (judgment
   today; a mechanical scan once the catalog distinctiveness check lands).
5. **Hygiene**: `agnix` and `skillspector --no-llm` green after install.
6. **Claims**: a performance or quality claim is measured with a with/without
   run before the skill earns the catalog, or rejected on the claim alone.

## Not Installed (deliberately)

- `cwm-roslyn-navigator` MCP server: redundant with Glider
- bash workflow hooks (`hooks/`), not Windows-native; the repo's build/test
  workflow is covered by the build-fix/verify skills and the temp-output test
  command. (The gate-guard pre-commit hook under `scripts/hooks/` is a thin
  sh shim over PowerShell, git-hook shaped, not a bash workflow hook.)
- Web/API/EF/Docker/Aspire skills (api-versioning, ef-core, ddd, clean-architecture,
  vertical-slice, docker, container-publish, aspire, serilog, opentelemetry,
  messaging, minimal-api, openapi, scalar, authentication), no such surface here
- Workflow skills that duplicate existing skills (plan, tdd, checkpoint,
  wrap-up, de-sloppify), the project already has a `desloppify` skill and
  the global `tdd` skill
- Registry pass 2026-08-24 (skills.sh leaderboard, top ~400 + mattpocock
  upstream at `6654f6b`), each against the intake filter above:
  `triage` (a 7-label issue state machine the to-spec to-tickets to
  implement flow does not use; the `/triage` flag reference in
  `docs/agents/issue-tracker.md` is annotated, not activated), `prototype`
  (duplicates the ported poteto prototype playbook; its UI branch is
  web-shaped: one route, a URL search param, a pnpm task runner), `wizard`
  (a bash + `.env` + WSL wizard; the human-only steps here are owned by the
  elevated runner and `hardware-e2e-validation`), `teach` (education
  workspace, no project need), `to-questionnaire` (async decision docs for
  another human; the solo project settles decisions in-session through the
  `question` tool and grilling), `retro` + `implement-spec` (upstream
  `in-progress`, skipped), `obra/superpowers` (every salient skill overlaps
  an installed one: systematic-debugging = diagnosing-bugs,
  test-driven-development = tdd, verification-before-completion =
  principle-prove-it-works + verify, requesting/receiving-code-review = the
  code-review skill + code-reviewer agent, writing-plans/executing-plans =
  to-spec/implement, subagent-driven-development = implement + the task
  tool), `JuliusBrussee/caveman` (a product, not a skill: an npm CLI, a
  BSL-1.1 proxy in the provider traffic path, telemetry on by default; its
  pixel mode renders SKILL.md bodies to PNG, which agnix, skillspector, and
  the prose gate cannot lint; the terseness idea is already unslop +
  technical-writing), `vercel-labs/find-skills` + `anthropics/skill-creator`
  (meta overlap: the intake filter above plus `authoring-a-skill` and
  `writing-for-agents`), and the leaderboard's dominant categories by shape
  (AI video/media, web UI design, databases, cloud, SaaS CLIs): no
  .NET/desktop surface on the top ~400

## Verification Commands

- Build: `dotnet build ModernWigiDash.slnx -c Release --nologo`
- Tests (temp output avoids a running app instance locking the App output):
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- Format: `dotnet format ModernWigiDash.slnx --verify-no-changes --verbosity quiet`
  (line endings are deliberately unpinned, ADR-0010; do NOT re-add
  `end_of_line` to `.editorconfig`, it recreates a ~45,000-error wall on
  Windows checkouts)
- Full gate run (build → test → format → prose, stops at first failure,
  appends one trail row per run to `.audit/gates.tsv`):
  `scripts\run-gates.ps1`, use it for full gate runs instead of the three
  commands above. The prose stage is the 2026-08-23 em-dash sweep's scope,
  kept honest by the gate: no em dash (U+2014) in living prose (`.md`
  outside `.desloppify/`, `.superpowers/`, `docs/(superpowers|archive|reports)/`,
  `.opencode/(skills|agents|node_modules)/`, `.git/`, `bin/`, `obj/`; the one
  exempt line is the ADR-0009 quoted hint example).
- Commit guard: a pre-commit hook blocks a commit unless the last gate row in
  `.audit/gates.tsv` is green in all four stages, its sha equals current HEAD,
  and the run is at most 60 min old (`-MaxAgeMinutes` on the guard). Install
  once per clone with `git config core.hooksPath scripts/hooks` (the hook file
  `scripts/hooks/pre-commit` is committed; the activation is local config).
  Logic lives in `scripts/gate-guard.ps1` (testable via `-GatesFile`). Escape
  per invocation only: `$env:WMD_GATE_GUARD_SKIP = '1'`.
- Debt guardrails (`DebtGuardTests`, runs in the gate's test stage, so the
  commit guard enforces them before every commit): the mechanical debt layer
  is machine-pinned against the src tree (raw scan, the `ArchitectureTests`
  house-rule shape; shared mechanics in `RepoScan`):
  - sync-over-async (`.Wait()` / `.Result` / `GetAwaiter().GetResult()`) only
    at the documented, budgeted sites (an allow-list with a reason per file,
    drift-checked both directions),
  - `async void` only on event handlers (the `EventArgs` signature),
  - every handle-acquiring file (P/Invoke extern, MemoryMappedFile, UsbContext,
    named mutex, loaded native library) carries its disposal evidence in the
    same file or a documented exception,
  - the frame pipeline's encode + buffer pool have one entry (the
    `IRgb565Encoder.Encode` call and every `FrameBufferPool` reference sit in
    `FrameDelivery` + the pool's own file),
  - no dead private helpers (a private method with no call site in its type's
    files or the project XAML, transitive chains included).
  A violation fails the gate and the failure message spells the fix; a new
  legitimate site is a deliberate allow-list edit with a reason.
- Branch review: incoming PRs and feature branches are reviewed with the
  `code-reviewer` agent backed by `.opencode/rules/dotnet-rules.md` (its
  "Debt Dimensions" section names the three review areas: async/await without
  thread starvation, allocation handling and object pooling, disposal of
  unmanaged handles). The agent covers the judgment layer the pins cannot
  see: is an allow-list reason true, is an abstraction earning its place,
  does a Dispose path release every handle on every failure leg.
- Hygiene sweeps: `desloppify` (the CLI-driven health workflow, state under
  `.desloppify/`) is the periodic deep sweep for redundant abstractions and
  boilerplate; its mechanical residue (dead helpers, the anti-patterns above)
  stays pinned in `DebtGuardTests` so it cannot regress between sweeps.
  `unslop` is the prose pass (already gated by the prose stage), not a code
  sweep. Run a desloppify pass before a release or after a large refactor;
  record findings under `docs/reports/` and pin what is mechanical.
- Agent hygiene scanners (the 2026-08-23 awesome-claude-code adoption pass;
  the findings record is `docs/reports/2026-08-23-agent-hygiene-scan.md`):
  `skillspector` (NVIDIA SkillSpector, installed via
  `uv tool install git+https://github.com/NVIDIA/SkillSpector.git`, binary
  `C:\Users\tobia\.local\bin\skillspector.exe`) scans the skill trees for
  supply-chain and prompt-injection patterns, static only (`--no-llm`) so
  skill content never leaves the machine:
  `skillspector scan .opencode/skills --no-llm` and the same over
  `C:\Users\tobia\.config\opencode\skills`. The security-scan skill's layer 7
  wraps it with the house verdict table. `agnix` (npm global, `agnix .`)
  validates SKILL.md / AGENTS.md / agent-frontmatter shape (448 rules); run
  it after authoring or porting a skill (authoring-a-skill step 2,
  create-verification-skill step 4). `ctxlint` (npm global) lints root-level
  context files; its reference base-path (the context file's own directory)
  does not fit `.opencode/AGENTS.md`, so the deterministic stale-reference
  pre-pass is `scripts\ref-check.ps1` (the rules-check-drift step 0).
- Live-stack run requires elevation. **User preference: no per-call UAC prompts**:
  use the no-consent runner:
  `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elev-no-uac.ps1 -Command "<cmd>"`
  (drops the command into `pending.ps1`, triggers the `WmdElevatedRunner` scheduled
  task, created once with `/RL HIGHEST`, so `schtasks /Run` needs no consent, and
  polls `result.txt`; the elevated token is held by the task, the command runs in the
  user session so UIA can drive the app). `run-elevated.ps1` (UAC per call) stays as
  the fallback. If the task is missing (e.g. Temp was cleaned), recreate it with ONE
  elevated call: `schtasks /Create /TN WmdElevatedRunner /SC ONCE /ST 03:13 /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\elev-runner.ps1" /RL HIGHEST /F`
