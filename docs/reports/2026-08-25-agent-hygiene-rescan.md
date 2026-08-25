# Agent hygiene rescan: agnix + SkillSpector (2026-08-25)

Follow-up to `docs/reports/2026-08-23-agent-hygiene-scan.md`. The inspection
sweep wave (complexity splits, XML doc backfill) closed with a hygiene pass
over the agent surfaces. Same tools and versions as the baseline: agnix
0.49.0, SkillSpector 2.9.6, `--no-llm` (static only, no content egress).

## agnix: 31 errors resolved, 0 remain

The 2026-08-23 pass accepted the unclosed-XML-tag errors on `<placeholder>`
prose as a false-positive baseline (33 errors at HEAD, the global upstream
skills unedited, `--fix` declined because it rewrites prose). This pass
resolves the 31 project-local errors by wrapping each template token in a
code span: the token keeps its exact spelling, no file matches the bare form
in any substitution protocol (verified by search before editing), and the
convention already existed in `reflect/SKILL.md` (its routing line carries
the same token backticked). Each wrap is a verbatim token, not an autofix.

Files (20 tokens across 6): `poteto-mode/playbooks/hillclimb.md`
(`<metric>` x2), `poteto-mode/playbooks/session-pickup.md` (`<transcript
path>`), `reflect/references/divergent-reviewer.md`,
`reflect/references/judgment-reviewer.md`,
`reflect/references/tooling-reviewer.md` (3 tokens each: `<ABSOLUTE_PATH>`,
`<kebab-name>`, `<DIGEST IF FILE PATH UNAVAILABLE>`), and
`reflect/references/synthesizer.md` (14: the three reviewer-output slots,
the drift/keep example tokens, the Accepted table rows, and the
Rejected reason enumeration).

Result: `agnix .` reports 0 errors. The 121 warnings + 33 info messages are
all in the documented classes from the baseline report (the inert
`disable-model-invocation` field, portability notes on the paths the docs
name, the AGENTS.md reference resolver base-path quirk, the size and
lost-in-the-middle notes). No action taken on them.

## SkillSpector: 17 HIGH hits, all triage to the baseline verdict

Same scanner version as the baseline run. Every HIGH location matches the
2026-08-23 triage (its findings table, mirrored in the security-scan skill's
verdict table at
`.opencode/skills/security-scan/references/scan-layers.md`): the reflect
reviewer read-only constraints (RA1/RA2), the desloppify version marker and
quoted temp cleanup (P2/TM1), the project-structure sample XML comments
(P2), the feature-map example and decision-log negative-form constraints
(MP3), the ablate git subprocess (AST4), the httpclient-factory example
endpoints (E1), the never-block principle (EA2), and the desloppify uvx
prerequisite (RP1). No malicious pattern; no content change.

One addition to the verdict table: the re-scan now re-hits the table's own
quoted patterns (the four lines that quote the trigger phrases), so the
table records itself as a self-referential expected hit. The MEDIUM set is
the baseline classes plus AS3 skill enumeration on the reflect reviewer
prompts, which is the documented skill-tree enumeration the prompts require
(the subagent scans the three skill trees for skill-use evidence); no action.

## Tooling quirks persisted

Three quirks hit live during the wave are now in the `.opencode/AGENTS.md`
Tool Mapping verified-quirks list:

1. A solution-wide `-p:BaseIntermediateOutputPath` breaks multi-TFM builds
   (NETSDK1005: one `project.assets.json` per TFM, the last restore wins);
   a temp `-p:BaseOutputPath` is the working substitute.
2. git 2.55 with `text=auto` classifies a file containing a lone CR (a CR
   not followed by LF) as binary, so `git add` stores raw bytes and the diff
   becomes a whole-file EOL change. Scan modified files for a stray 0x0D
   not followed by 0x0A before committing.
3. The PowerShell tool transport strips backticks and mangles `$` variables
   inside inline commands; multi-step byte/regex work goes through a `.ps1`
   file run with `-File`.

## Re-run

Same commands as the baseline report (SkillSpector over the project and
global trees, `agnix .`, `scripts\ref-check.ps1`). Cadence unchanged: when a
skill is installed, ported, or upstream-synced.