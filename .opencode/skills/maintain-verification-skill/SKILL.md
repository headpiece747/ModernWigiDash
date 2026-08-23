---
name: maintain-verification-skill
description: "Periodic pass that keeps this repo's verification skill (verify-modernwigidash) and its feature map honest: parallel source readers per feature, one live session driving every feature, at most one small batch of proven corrections. Use for /maintain-verification-skill or 'audit the verify skill'."
disable-model-invocation: true
---

# Maintain a verification skill

A feature map rots the moment the app changes. This skill is the upkeep loop for `verify-modernwigidash` (or any project-local verification skill under `.opencode/skills/` with a feature map). The unit of rigor is the feature, not every sentence: cover every feature file from source and exercise every feature live, without terminalising every bullet.

## Outcomes

Pick one, and say which:

- **clean.** Every feature got source and live coverage; nothing worth shipping. No commit.
- **changed.** One small batch of commits (or a single squashed commit) ships proven doc, harness, or map corrections.
- **blocked.** Coverage could not finish or a proven fix could not land safely. Say exactly what blocked it.

## Edit scope

Only edit the verification skill's own directory (its SKILL.md, features/, and any harness scripts it owns). Never edit product code during a run: a behavior the map describes that the app no longer does is either doc drift (fix the map) or a product regression (report it, don't paper over it in docs).

## Pass

0. **Locate the target.** The target is `.opencode/skills/verify-modernwigidash/` unless told otherwise: the project-local skill whose body has launch/drive sections and a feature map. Several candidates -> ask which one; none -> stop and point at `/create-verification-skill` instead of inventing a target.

1. **Index hygiene.** Read the feature map README and glob its sibling files. Fix missing, extra, duplicate, or dead entries. Lightweight; no generated inventory.

2. **Source wave.** One read-only subagent per feature file, launched concurrently (`subagent_type: general`). Each explains "how does this user-facing feature work?" from source (indexed symbol navigation, not raw grep), flags likely doc drift with citations, and returns one concise live-verification recipe. Children never drive the app and never edit files. Return shape: feature summary / source entry points / likely drift or none / one recipe.

3. **Reconcile.** Every feature file has a returned summary. Merge overlapping recipes into as few app states as practical. Spot-check cited drift; don't re-prove clean claims. Sweep recent churn (`git log --oneline -30` on the App project) for user-facing surfaces missing from the map, require a concrete source path before calling one missing.

4. **Live pass.** Required even when source looks clean. The coordinator owns all driving; follow the verification skill's own launch model, one long-lived instance driven serially for the WPF app (it is a shared desktop instance; refuse to double-drive). Exercise every feature at least once, and hold three invariants the whole pass, whatever the failure: (1) never drive an instance that hasn't been health-checked since it last did something surprising, doctor before first drive, doctor after any failed drive, and where the doctor can't see the failure (a wedged UI state on a healthy process), reset to a known state or relaunch rather than hoping; (2) evidence captured so far survives every cleanup, checked at its named location, not assumed; (3) nothing a drive started outlives that drive's usefulness. The app process, profile mutations, and scratch state are all rolled back or removed (the profile is restored from the run's backup per the skill's Cleanup section). A doctor failure caused by skill drift is drift: fix it under edit scope and retry once, relaunch the app, nothing more, before calling the pass `blocked`. A feature that can't be reached is `verified-unreachable` only with the concrete prerequisite (USB device, elevated launch, a specific profile state) and the route attempted; if the map omits that prerequisite, that's drift. Any harness fix from triage gets re-driven live before it lands. Final teardown happens after the last drive of the run, including re-proofs, so nothing outlives the run (evidence stays, per the skill).

5. **Triage.** Wrong or missing user-POV description -> doc drift, fix it. Working behavior the harness can't drive -> harness gap, fix it; a harness fix follows the same helpers rule as generation (scripts executable, invocation documented in the skill body). App behavior that's actually broken -> product gap; record it for the user, keep it out of the corrections.

6. **Ship or stop.** For changed: one verified batch of proven corrections, re-read every changed file before committing. For clean or blocked: nothing to ship, report the outcome and the coverage honestly.

Keep concise run notes (features covered, unreachable prerequisites, confirmed drift, outcome) in a scratch location; don't commit them.