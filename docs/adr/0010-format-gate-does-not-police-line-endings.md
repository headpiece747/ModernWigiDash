# ADR-0010: The format gate does not police line endings

**Date:** 2026-08-20
**Status:** Accepted
**Deciders:** Project owner

## Context

`dotnet format --verify-no-changes` is the repo's style gate (the verify
pipeline's Phase 6). On Windows it failed with ~45,000 ENDOFLINE errors across
essentially every file in the solution: `.editorconfig` pinned
`end_of_line = lf`, but `.gitattributes` (`* text=auto`) checks out CRLF on
Windows while the git index stores everything as LF. The committed content was
compliant — the working tree was the problem — so the gate could not pass on
any clean Windows checkout, and its noise masked the real drift: 15 actual
violations (14 files missing the final newline, one collection-expression
space) that only surfaced once the tree was run in its committed LF state.

The contradiction had one more corner: `*.cmd` files are pinned CRLF
(`cmd.exe` misparses parenthesized if-blocks in LF-only files, and
`apply-update.cmd` is embedded verbatim), so a "fully LF everywhere" checkout
could not be uniform anyway.

## Decision

**`end_of_line` is not a format-gate rule.** `.editorconfig` keeps
`charset = utf-8` and `insert_final_newline = true` and drops the
`end_of_line` pin (an inline comment marks the omission as deliberate).
Line-ending discipline stays with git: `text=auto` normalizes CRLF to LF at
commit time, so the committed truth is always LF; working trees take their
platform's native EOL (CRLF on Windows). The format gate measures only the
drift git does not police — whitespace, final newlines, charset.

## Consequences

**Positive:**

- The gate passes on a clean Windows checkout and measures real drift only —
  the 15 masked violations surfaced and were fixed the moment the wall went
  down.
- No working-tree re-normalization: CRLF on disk is a normal Windows state,
  and diffs stay clean because the LF index is the comparison source.
- The CRLF-pinned `*.cmd` files no longer fight a global style rule.

**Negative:**

- The gate no longer flags a committed EOL drift — a bounded concession,
  because `text=auto` still normalizes at `git add`, so CRLF cannot reach the
  index.
- `.editorconfig` now looks like it is missing a rule (charset and final
  newline pinned, EOL not). This ADR plus the inline comment exist so the
  missing pin is not "fixed" back in — re-adding it recreates the ~45,000-
  error wall on every Windows checkout.

## Alternatives considered

1. **Keep the pin and add `eol=lf` to `.gitattributes`** (a fully-LF
   checkout) — rejected: `*.cmd` must stay CRLF, so the rule cannot be
   uniform, and Windows tooling reproduces CRLF on every edit either way; the
   repo would fight its own platform.
2. **Keep the pin and accept the broken local gate** (the status quo) —
   rejected: the gate failed on every clean Windows checkout and its noise
   masked real violations, which is exactly how the 15 drifted for weeks.

## Date

2026-08-20
