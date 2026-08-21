### Autonomous run

**You own the exit condition. Define done, then drive to it without stopping.** For "going to bed" / "run until done" / "drive this until X".

1. State the exit condition as a checkable predicate before the first iteration (tests green, repro fixed, metric at target, pixel-diff zero). A vague goal stalls; a predicate lets you stop.
2. Pick the wake mechanism. An event to watch (a background build finishing, a subagent returning, a timer-based re-check) gets a check-after-the-event pattern with a long time-based heartbeat as fallback. No event gets a fixed-interval heartbeat sized to when the result is worth re-checking. (This host has no built-in loop command; the show-me-your-work trail is the resumable state, so the next session can pick up at the last logged predicate.)
3. Each iteration makes the smallest change the evidence justifies, verifies it against the predicate, commits if it advanced, discards changes that didn't help. Belt-and-suspenders that "might help" gets reverted, not left to ride.
    Sequence the work via the **sequence-verifiable-units** principle skill, verifying each unit before the next instead of batching checks at the end.
4. Mid-run discoveries are yours. Address broken skills, related bugs, flaky verifiers, tooling failures, and fixable drift yourself via poteto-mode. Put out-of-band fixes in their own commit. Do not park reversible work for the human: surface only irreversible actions, genuine product or preference calls no experiment can settle, or a real dead end. Keep the predicate as the main drive, and return to it after each side fix.
5. Checkpoint every iteration via the **show-me-your-work** skill, a row for what changed and whether the predicate moved. A run with no trail can't be audited or resumed.
6. Stop when the predicate is met. A plateau is not a stop, so keep going and pivot your approach to push past it. Surface a genuine dead end rather than spinning, and never relax the predicate to declare victory.
7. Leave a clean handoff: the trail, the last verified state, and the first action on resume (the `pause-safely` playbook's resume note shape, even when you are not pausing for a human).

**Reply:** the exit condition, iterations run, what landed, what was discarded, final predicate state.