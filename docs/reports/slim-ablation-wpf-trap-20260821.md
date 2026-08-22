# AI-layer ablation — WPF-trap task (2026-08-21)

Scope: the `ablate-ai-layer` experiment designed at P4 of the slim-down plan
(`docs/superpowers/plans/2026-08-21-global-usings-slim-modern.md`, "Next
session" section). Base `f16cf36`, model `local-ninfer/qwen3.8-27b` pinned in
both arms (local RTX 5090 inference — $0 cash), 2 control + 2 stripped runs,
`--scope always` (`.opencode/AGENTS.md` + `.opencode/rules/dotnet-rules.md`
stripped). Task: confirm `ModernWigiDash.Sdk` is a project global using in the
App, sweep every `.cs` using that restates a project global or the
ImplicitUsings baseline, end with a green `dotnet build`, leave the tree
uncommitted.

## Verdict

| Rule (always-loaded) | control | stripped | Verdict |
|---|---|---|---|
| WPF baseline rule — "In WPF projects (App, Tests) keep the baseline usings explicit — the WPF XAML markup pass compiles a temp project that does not apply ImplicitUsings" (`dotnet-rules.md` §1) | followed 2/2 — quoted the rule verbatim before sweeping; the correct action at this HEAD is a no-op (P1 already removed everything legal) | **violated 2/2** — removed all 9 baseline usings, build failed `CS0246 HttpClient` at `UpdateService.cs(20,29)` in the `wpftmp` XAML pass (the exact P1 failure), then diagnosed and restored the usings | **Load-bearing — keep** |

Everything else in the 266-line always-loaded layer is **untested by this
task** (no new code, no tests, no other project touched) — keep, no evidence
either way. "Untested" is reported separately from "no difference"; merging
them is how a rule protecting an unexercised case gets deleted.

## Per-run record

| Run | Duration | Behavior | Final state |
|---|---|---|---|
| control 1 | 1404 s | Inventoried the 9 candidates, quoted the WPF temp-project rule verbatim before sweeping, kept the usings | 0 changes, green build first try |
| control 2 | 1744 s | Same, plus an empirical probe of the `wpftmp` markup-compile behavior from `obj/` artifacts; kept the usings | 0 changes, green build |
| stripped 1 | 1046 s | Removed all 9 baseline usings → `CS0246 HttpClient` `UpdateService.cs(20,29)` (WPF temp project) → diagnosed the temp-project gap → restored the usings | 0 net changes, green after repair |
| stripped 2 | 862 s | Same sequence; diagnosis quote: "the WPF XAML markup compile pass uses a temp project that does not carry ImplicitUsings globals" | 0 net changes, green after repair |

Both arms ended with a green `dotnet build ModernWigiDash.slnx -c Release`
and left the tree uncommitted (task constraint). Note: the task's own
"build must end green" requirement made the stripped arm's self-repair
obligatory — without it, the same runs could have ended red. The control arm
spent *more* time (23–29 min vs 14–18 min): the rule buys caution up front in
exchange for the red-build detour later.

## Harness caveats (stated, not hidden)

- **`opencode.json` is gitignored**, so a worktree built from HEAD has no
  project config and the repo's instruction files do not load by default.
  Pre-flight probe in an un-stripped worktree: the model answered `NOT
  LOADED`. The runner wrapper (kept at
  `Temp\opencode\wmd-ablation\run-wmd.ps1`, ephemeral — recreate from the
  wrapper description below if needed) therefore writes a minimal
  `opencode.json` (instructions only — no MCP servers, no plugins) into the
  worktree when the layer files exist. The stripped arm (files gone) starts
  with no project config, proven clean by a separate pre-flight probe. No MCP
  in either arm — symmetric and fast.
- **The script's "empty arm" flag is a false positive here.** The control
  arm's *correct* behavior at this HEAD is a no-op: P1's sweep already
  removed every legal using, and the WPF rule protects the rest — so a
  rule-following agent legitimately changes nothing. The script also counted
  its own layer-strip as the stripped arm's "changes": both stripped diffs
  are byte-identical (21,968 B) and contain only the two `.opencode/`
  deletions — zero `.cs` hunks, which proves the stripped runs restored every
  using they removed. Neither arm crashed (exit 0, coherent transcripts): the
  experiment is valid.
- **The trap's knowledge existed in the stripped worktree** — the plan doc
  and `scripts/sweep-global-usings.ps1` (which derives the rule from the
  csproj) are tracked — but nothing pointed the agent at them; it re-derived
  the rule from the build failure instead. `CONTEXT.md` (present in both
  arms) does not state the WPF fact. The ablated rule line is what puts it in
  context without a search.
- Evidence kind: edits to existing files (no arm created a new file) plus
  observable events (the CS0246 hits, the verbatim rule quote), not
  diff-impressions.
- Cost: ~45 min wall on local inference, $0. Worktrees all cleaned up; the
  experiment's only side effect on the main tree was the script adding
  `.ablation/` to `.gitignore` (kept). Raw artifacts: the machine run
  record is committed beside this report as
  `slim-ablation-wpf-trap-20260821.summary.json` — per-run exit codes,
  durations, `files_changed` (the zero-`.cs`-change evidence for the
  stripped arm), stderr tails, and transcript excerpts. The per-run diffs
  are not committed separately: both are byte-identical (SHA-256
  `8A239150A6AC4397BBD5A930A9F8885C5D78E6B4CAA8ADD6DDAE2A9440434486`,
  21,968 B each) and their content is exactly the deletion of the two
  tracked layer files. A full copy (JSON + both diffs) remains in
  `Temp\opencode\wmd-ablation\results\` — ephemeral.

## Grading notes (per the skill's rubric)

- Graded per rule, not per diff: the always-loaded files were turned into
  testable claims; only the WPF claim was exercisable by this task. The other
  §1 style rules, the architecture and testing rules, the verification-command
  block, and the session-lifecycle rules never got an opportunity to apply
  (`n/a`) — marked untested, not expired.
- The fact is encoded in three durable places plus the compiler: the
  ablated rule line, `scripts/sweep-global-usings.ps1` (derives the rule from
  the csproj — no mode flag to get wrong), the plan doc's P1 record, and the
  build itself (deterministic detector). No encoding needs changing.
- The always-loaded total is unchanged: 266 lines / ~5,266 tokens
  (`map_layer.py` re-run after the experiment — nothing was deleted or
  re-added).
- One probe task is a data point, not a verdict, and this ran on the weakest
  model in use (the local 27B); the verdict binds that model. Before deleting
  anything large from the layer, a second task on a different part of the
  codebase is the skill's own recommendation. Nothing was deleted here.