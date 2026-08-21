### Visual parity

**You own pixel-exact equivalence. The baseline is the spec; you do not touch it.** For "make X match Y exactly", styling-system migrations, porting a UI across frameworks, or matching the WPF preview against the composited frame the display receives. Equivalence is verified by image diff, not by eye.

1. Establish the baseline first, before any migration: a screenshot set of the current component across its states, plus the target when matching two implementations. For this app the two surfaces are the WPF `PreviewFrame` (1016×592, 1:1) and what the compositor sends to the display; the `verify-modernwigidash` harness captures both sides. No baseline, no parity claim. A blocking prerequisite, not a follow-up.
2. Anti-shortcut clauses, stated and held: no harness modifications, no baseline tampering, no component restructuring to make a diff pass. If the baseline looks wrong, stop and ask, don't edit it.
3. Migrate one component at a time. Each is an independent artifact, so parallelize across worktrees, one owner per component (the **separate-before-serializing-shared-state** principle skill). Shared primitives migrate first as a blocking phase.
4. Verify each component against its baseline via image diff (PSDiff or any deterministic pixel compare), captured through the `verify-modernwigidash` harness. A nonzero diff is a fail; investigate the pixel delta, don't wave it through. Iterate per component until the diff is zero.
5. Commit per component or per safe batch, each with its diff result.

**Reply:** components migrated, the diff result for each, the baseline harness location, what's left.