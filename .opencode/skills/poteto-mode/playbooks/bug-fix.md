### Bug fix

**You own this task. Plan, review, verify.** Delegate investigation and the fix to subagents, stay in the lead.

Be scientific. Every shipped line traces to runtime evidence. Belt-and-suspenders that "might help" is a hypothesis, not a fix; it does not ship. When evidence refutes a hypothesis, revert what it motivated. The smallest change the evidence justifies ships, nothing more. Same discipline for Perf, where the evidence is the trace.

1. Reproduce it yourself on the matching surface via the matching verify skill (`verify-modernwigidash` for the WPF UI; `hardware-e2e-validation` for device-touch behavior). Don't hand the repro to the user. A debug or instrumentation protocol that says to ask the user does not override this; you drive the instrumented runtime. Ask the user only with a stated, specific reason the control surface cannot reach the target, and only after driving it as far as it goes. Won't reproduce directly, force it: synthesize the trigger, tighten conditions, or instrument until it fires. A bug you can't reproduce, you can't prove fixed.
2. Binary-search the cause. Form the candidate hypotheses, then rule them out until one survives. Seed them with `how` over the affected subsystem and git history (`git log -S`, `git blame`) plus `docs/adr/` and `CONTEXT.md` for regression context. Each pass, take the split that cuts the most remaining problem space, get runtime evidence, eliminate. When program state is unclear, add instrumentation or logging and read it as the code runs. Don't guess. Drive a long or stubborn hunt over repeated rounds, logging each elimination. Confirm the surviving *mechanism* with runtime evidence before the step-3 architect fan-out; a design grounded on a plausible-but-unconfirmed cause can be unanimously wrong while the real cause sits one subsystem over.
3. Plan the fix. If it crosses a function boundary, `architect` first. Delegate implementation to a subagent (session model) with a specific scope; review the diff.
4. Verify on the same surface; the original repro now passes. "Inconclusive" or wrong-surface is not a pass; flag it. Unit tests show branch behavior, not bug absence. For a device-visible symptom, the on-device evidence is the bar, not the unit test.
5. Stage the commits so the failing repro lands before the fix in git history; the diff tells the story. See the **tdd** skill for the failing-test-first cadence when the bug has a cheap local test path; skip it when the test would be expensive, integration-heavy, or unclear.
    This is the canonical **sequence-verifiable-units** principle skill, the failing test first and the fix on top.
6. Close with the house gate: full release build plus the affected tests green (AGENTS.md verification commands), then commit per `dotnet-rules.md` §7. Every on-device finding gets a regression test through a seam before the fix ships (the `hardware-e2e-validation` rule).

Investigation fans out `how` + git/ADR archaeology as parallel subagents.

**Reply:** what was broken, root cause, fix, how you verified. Paste failing-then-passing repro output verbatim.