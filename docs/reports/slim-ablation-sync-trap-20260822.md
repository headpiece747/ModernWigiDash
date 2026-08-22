# AI-layer ablation — sync-transport trap (probe 2, 2026-08-22)

Scope: the second ablation probe (follow-on to the WPF-trap probe), targeting the
ADR-0001 synchronous-transport rule in `.opencode/rules/dotnet-rules.md` §2.
Base `5f531b1`, model `local-ninfer/qwen3.8-27b` pinned in both arms, 2 control +
2 stripped, `--scope always` (strips `.opencode/AGENTS.md` +
`.opencode/rules/dotnet-rules.md`). Task: add a device-responsiveness probe —
engine method + state surface + App 1 s wiring + MSTest tests, ending with a
green `dotnet build` and a green filtered test run.

## Verdict

| Rule | control | stripped | Verdict |
|---|---|---|---|
| ADR-0001 sync-transport — "do NOT wrap `DisplayHidTransport` in fake async. Do not convert it to async" (`dotnet-rules.md` §2 L34) | followed — sync `ControlOut` under the transport lock, probe on the existing background-thread `PollLoop` | followed 4/4 — all kept the production transport call synchronous | **UNTESTED (confounded) — keep.** Two confounds prevent a clean verdict; not evidence the rule is expired. |

## Observed result (before the confounds)

**0 of 5 completed runs introduced fake-async in the production transport
path.** Every run kept the new seam member (`SendWakeCommand`) a *synchronous*
`ControlOut(CmdWakeDevice, 0, null)` under the transport's lock (the same shape
as the existing `SendFrameBytes`/standby control writes) and ran the probe on
the *existing* `PollLoop` background-thread tick (the SENSOR/FRAMETIME shape),
so the UI thread is never blocked.

The only `async`/`await`/`Task.Run` in any diff were legitimate, all in test
code or interface conformance:

- `public ValueTask DisposeAsync() => ValueTask.CompletedTask;` — the test
  fakes implement the seam's `IAsyncDisposable` member (unavoidable, not a
  violation).
- stripped-2: an async **test method**
  (`TelemetryCluster_Start_RunsTheProbeOnTheLoopThread`) + `await
  TestWait.WaitUntilAsync(...)` — test code.
- one salvaged run: a *comment* noting "here it runs synchronously, like the
  other tick."

| Run | Arm | Duration | Build/tests | Fake-async in prod path |
|---|---|---|---|---|
| control-1 | control | **timed out** (7715 s > 7200 s limit) | n/a (agent killed at the limit, worktree cleaned) | n/a |
| control-2 | control | 5398 s | green, 8/8 tests | none |
| stripped-1 | stripped | 3715 s | green, 8/8 tests | none |
| stripped-2 | stripped | 2477 s | green, 6/6 tests | none |
| salv-stripped-1/2 | stripped | recovered from the first (timed-out) driver | green-unconfirmed | none |

## The two confounds (why this is NOT a clean test)

1. **The task leaked the mechanism.** The task said: *"the PollLoop shape the
   sensor and frame-time producers use (the loop tick runs on a background
   thread, so the probe call must not block the UI thread). Follow the existing
   loop shape."* That *is* the house answer to "don't block the UI thread." The
   agents were told the mechanism, so the sync-vs-fake-async decision the rule
   governs was largely pre-empted — they followed instructions rather than
   discovering the constraint.

2. **CONTEXT.md carries the rule and was never stripped.** CONTEXT.md is
   auto-loaded into **both** arms (verified PRESENT in a pre-flight probe; only
   the two `.opencode/` files are stripped). It states the ADR-0001 decision
   three times — the Transport glossary ("All operations are synchronous - no
   fake async wrappers"), the Key Design Decisions table, and the ADR table.
   So the "stripped" arm still had the sync knowledge via CONTEXT.md; ablating
   only the `dotnet-rules.md` §2 line removed a redundant copy, not the
   knowledge.

Because of (1)+(2), "both arms follow" is the *expected* outcome and is not
clean evidence that the §2 line is expired. It shows the model follows when
told the mechanism *and* the rationale is available — a weaker claim than "the
model would do this anyway."

## Verdict & action

- **Keep the rule.** No clean evidence it is expired. This is the rubric's
  "untested / no evidence either way" row, kept visually separate from "no
  difference."
- **Do not read this as "delete §2."** A null result on a confounded probe is
  not a null result on a clean one.

## Secondary observation (not a verdict)

The control arm (full layer loaded) was slower: control-1 **timed out** at
7715 s while the three fast stripped runs were 2477–3715 s (control-2 was
5398 s). A possible small latency cost from the loaded layer, but confounded by
run variance and the hard 7200 s per-run cutoff (control-1's worktree was
cleaned on timeout; no diff captured) — not a clean measurement. Recorded, not
acted on.

## Process lessons (for the next clean ablation)

1. **Don't leak the mechanism.** A probe task must not name the pattern the
   rule governs. Probe-2's "follow the existing PollLoop shape" handed the agent
   the answer; a clean sync-rule probe would say only "the probe must not block
   the UI thread" and let the agent choose the mechanism.
2. **Check for redundant knowledge in non-stripped files.** Before ablating a
   rule, grep the always-loaded set for the same decision. ADR-0001 is in
   CONTEXT.md (never stripped) *and* `dotnet-rules.md` §2 (stripped). For a
   clean test the knowledge must live ONLY in the stripped file(s); otherwise
   the stripped arm isn't actually stripped. The WPF-trap rule (probe 1) was
   clean on this axis — it lived only in the stripped file.
3. **Salvage on driver death.** When the driver dies (timeout/kill), the
   in-flight worktrees survive on disk — capture their `git diff` before
   `git worktree remove`. This recovered the two orphaned stripped runs from the
   first (timed-out) attempt.

## Execution notes

- The first driver attempt hit the 2 h bash-tool cap and was killed; its two
  in-flight stripped worktrees were salvaged (diffs captured), then the driver
  was re-run **detached via a scheduled task** (the bash tool reaps short-lived
  child trees, so a scheduled task reparents the driver to the task scheduler).
  A double-trigger (a leaked `Start-Process` python + the task) was caught and
  cleaned before it corrupted the out dir.
- control-1 hit the 7200 s per-run limit (7715 s actual) — the model was still
  editing (it had touched `CONTEXT.md` and the test file) when the cutoff
  landed; its work was not captured.
- Cost: ~3.5 h wall (12:22–15:40, 2026-08-22) on local inference, $0. Raw run
  record committed beside this report; the per-run diffs and the two salvaged
  stripped diffs remain in `Temp\opencode\wmd-ablation\` (ephemeral).

## Grading notes (per the skill's rubric)

- Graded per rule against each run's added lines, blind to arm until the join.
  The single testable claim (no fake-async in the production transport path)
  was checkable in every diff.
- The "untested (confounded)" verdict is reported separately from "both arms
  follow = delete"; they look identical in the data and mean opposite things.
- Always-loaded total unchanged (nothing deleted or re-added): the sync rule
  stays.