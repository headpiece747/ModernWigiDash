---
name: swarm
description: "Fan out N parallel workers, drain them, and return one report. Use for /swarm, 'swarm this', or parallel coverage, races, gauntlets, and exploration."
disable-model-invocation: true
---

> **Port note (this repo):** workers are local subagents on the session model (no cloud environment, no per-worker model selection). The natural slice families in this repo: the 12 widgets x their property-presentation contracts, the four REST quote legs, the DiagLog tag vocabulary across transport/backend/delivery, the presentation modules vs their tests.

# Swarm

Fan out N parallel local workers. They may cover separate slices, race the same brief, or mix both. The parent waits, aggregates, and returns one report.

## Start

Open a todolist with one entry per phase before launching anything.

1. Frame
2. Fan out
3. Aggregate
4. Report

## Phase A: Frame

1. State the done predicate and the artifact or report the swarm must return.
2. Choose the shape. Partition into slices, race N workers on identical briefs, or mix both. For a race or mixed shape, declare `first pass`, `rank all`, or `best-of` before spawning.
3. Set N from the user or derive it from the shape. N counts workers, not any concurrency limit.
4. Name each worker's scope precisely: the files/project it covers, the question to answer, and how to verify its own answer (a test name, a glider query, a build).
5. Give each worker its own writable output when it writes. Use a worktree, branch, or `%TEMP%\opencode\swarm-<slug>\worker-<n>\`.

## Phase B: Fan out

Spawn all N workers in one message (`subagent_type: general`, session model), each with its brief.

Every brief stands alone. Include the goal, scope, exact slice or race arm, how to verify, and what to report. Reports use `PASS`, `ISSUES`, or `BLOCKED` with evidence (file:line, test output, query result).

If a worker drops out, proceed with N-1 and note it.

## Phase C: Aggregate

Read the terminal results. For coverage, every required slice needs a result. For a race, apply the selection rule declared up front. Use first pass, rank all, or best-of. Do not paste raw worker dumps.

Keep a compact result table, one-line evidenced issues, and explicit gaps or dropouts.

## Phase D: Report

Return one consolidated in-chat report with the table, issue one-liners, gaps or dropouts, and the race rule when used.