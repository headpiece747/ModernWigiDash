# Agent hygiene scan: SkillSpector, Ctxlint, agnix (2026-08-23)

The 2026-08-23 awesome-claude-code mining pass
(`docs/reports/2026-08-23-awesome-claude-code-triage.md`) recommended three
tools. The user signed off on all three; this is the adoption record.

## Tools installed

| Tool | Version | Install | Role |
|---|---|---|---|
| SkillSpector (NVIDIA) | 2.9.6 | `uv tool install git+https://github.com/NVIDIA/SkillSpector.git` (binary `C:\Users\tobia\.local\bin\skillspector.exe`) | Supply-chain + prompt-injection scan of the agent skill trees, static only (`--no-llm`) so skill content never leaves the machine |
| agnix | 0.49.0 | `npm install -g --allow-scripts=agnix agnix` | SKILL.md / AGENTS.md / agent-frontmatter shape validation (448 rules), autofix available |
| ctxlint | 1.1.3 | `npm install -g @ctxlint/ctxlint` | Context-file lint (stale refs, dead commands, secrets). **Base-path limitation, see below** |

## Scope

Project skill tree `.opencode/skills/` (the curated set, including the ported
poteto pack and the kit ports), the global skill tree
`C:\Users\tobia\.config\opencode\skills` (the Matt Pocock set), the agent
frontmatter in `.opencode/agents/`, and the house docs (CONTEXT.md,
`.opencode/AGENTS.md`, `.opencode/rules/dotnet-rules.md`, `docs/agents/*.md`).
All scans static; no LLM stage, no content egress (SkillSpector's SC4
dependency check queries OSV.dev by name only).

## SkillSpector (project tree: 100/CRITICAL static; global tree: 56/HIGH static)

The static score counts every pattern hit at high recall, precision is the
LLM stage's job, and the LLM stage is deliberately not run here. Every finding
was read and triaged. No malicious pattern exists in either tree.

| Finding | Location | House verdict |
|---|---|---|
| SC8 shipped Python bytecode (HIGH) | `ablate-ai-layer/scripts/__pycache__/map_layer.cpython-314.pyc` | **Fixed**: local debris from a script run, deleted; `__pycache__/` and `*.pyc` were already gitignored, so it was never committable, but the disk is what gets scanned |
| RA1/RA2 "edit skill" / "write code" (HIGH) | `reflect/references/{divergent,judgment,tooling}-reviewer.md` | Intentional: the reviewer prompts state the read-only constraint, often in negative form ("the parent agent applies edits based on your output"); the scanner matches the phrase, not the polarity |
| P2 hidden instructions (HIGH) | `desloppify/SKILL.md` (the `<!-- desloppify-begin -->` version marker), `project-structure/SKILL.md` (XML comments in csproj examples) | Intentional: the marker is the desloppify skill's own machine-managed version block; the csproj comments are example code |
| TM1 `rm -rf /` (HIGH) | `desloppify/SKILL.md` | Quoted temp-dir cleanup example (`rm -rf /tmp/desloppify-fix`) |
| MP3 "clear state" / "delete history" (HIGH) | `create-verification-skill/references/feature-map-example/README.md`, `show-me-your-work/SKILL.md` | Negative-form constraints ("never edit or delete history") in an example feature map and the decision-log rule |
| AST4 git subprocess (MEDIUM) | `ablate-ai-layer/scripts/run_ablation.py` | Intentional: the script's job is git worktrees plus running `opencode run`; in-repo, versioned, visible |
| E1 external URLs (MEDIUM) | `httpclient-factory/SKILL.md` | Example endpoints in code samples (api.example.com, api.test) |
| EA1/EA2 "without asking" / "don't ask" (MEDIUM) | `poteto-mode/SKILL.md`, global `grilling`, `setup-matt-pocock-skills` | The never-block-on-the-human principle, the ported house design |
| RP1 MCP rug pull (MEDIUM) | `desloppify/SKILL.md` | The skill's documented `uvx --from git+...` prerequisite for the upstream CLI |
| P6 "show instruction" (HIGH), TM2 (HIGH) | global `diagnosing-bugs/scripts/hitl-loop.template.sh`, `to-tickets/SKILL.md` | A usage comment in the human-in-the-loop template script; the migrate-callers-then-delete process prose |

## agnix (repo at HEAD: 33 errors / 92 warnings; after this pass: 33 errors / 107; global skills: 11 errors / 1 warning)

Errors are dominated by unclosed-XML-tag hits on `<placeholder>` prose
(template tokens in the reflect reviewer prompts and the global
to-spec/to-tickets/diagnosing-bugs skills). Those are false positives; the
upstream global skills were not edited, and `--fix` was deliberately not run
(it would rewrite prose placeholders). The before/after delta was verified
with a worktree-pinned baseline run (HEAD) against the working tree: the
error set is unchanged, two stale-reference warnings and nine hard-coded
Cursor-path warnings (the three reflect reviewer files) are gone, and the
fifteen new warnings are all in the documented false-positive classes (the
portability notes for the paths the tooling docs name, the AGENTS.md size
note 12886 to 14126 chars, and the lost-in-the-middle keyword positions).

Real findings, disposition:

| Finding | Disposition |
|---|---|
| `Unknown agent frontmatter field 'permission'` (3 agents) | False positive. OpenCode's agent docs document `permission: { edit: deny, bash: allow }` in Markdown agent frontmatter (the docs' own example is the review agent with `edit: deny`). The code-reviewer and comment-sicko read-only claims are enforced by the harness; agnix's rule set predates/misses OpenCode's agent permissions |
| `Agent file must have YAML frontmatter` on `docs/agents/{domain,issue-tracker}.md` | False positive: agnix's path heuristic treats anything under an `agents/` directory as an agent definition; these are Matt Pocock's prose domain docs |
| Stale file refs `scripts/wmd-verify.ps1`, `scripts/map_layer.py` in `.opencode/AGENTS.md` | **Fixed**: both named a repo-root path that does not exist; the files live in their skills' `scripts/` dirs. The references now point at the real paths (and the new `scripts/ref-check.ps1` pre-pass catches this class going forward) |
| Hard-coded Cursor paths in `reflect/references/{divergent,judgment,tooling}-reviewer.md` | **Fixed** (verified gone in the after scan): the ported reviewer prompts told the subagent to scan for skill use in `.cursor/skills/` trees. This harness loads skills from `.opencode/skills/` and `~/.config/opencode/skills/` via the `skill` tool; the line now names those. The session-pickup `~/.cursor/` mention is a prohibition (do not glob there) and stays, with its one remaining portability note |
| `disable-model-invocation: true` (24 project skills, 8 global) | Known and kept. OpenCode's skill spec recognizes only `name`/`description`/`license`/`compatibility`/`metadata` and ignores unknown fields, so on this harness the field is inert and the user-only gate rests on the description text. Kept because it is the universal-spec spelling for portability. Documented as a triage warning in the skill-authoring surfaces, not a fix |
| AGENTS.md 12000-char warning (Windsurf compatibility) | Noted, no action: this repo runs OpenCode, which has no such documented limit |
| Hard-coded Windows temp paths (`C:\Users\tobia\...`) | Deliberate machine-local temp output paths (the house temp-output test rule and the no-UAC runner); noted |
| Lost-in-the-middle keyword placement (always/critical at 43-53%) | Noted, no action |

## Ctxlint (not wired in; the rule lives in a house script)

ctxlint 1.1.3 resolves a context file's references against the context
file's own directory, and only auto-discovers default context-file names at
the search root. This repo's agent context file lives at
`.opencode/AGENTS.md` and its references are repo-root-relative, so every
one of them reports stale, and CONTEXT.md is not discoverable at all
(`.ctxlintrc` contextFiles does not change discovery). The base-path model
does not fit this layout. The rule itself (stale file references in the
house docs) is the right one, so it was implemented as
`scripts/ref-check.ps1`: backtick-quoted path-like references in CONTEXT.md,
`.opencode/AGENTS.md`, `.opencode/rules/dotnet-rules.md,
`docs/agents/*.md`, resolved against the repo root plus the doc's own
directory, with a documented exemption list for runtime data files
(`profile.json`, `app_theme.json`, `display_device.log`), machine-local
runner files, and the upstream `CONTEXT-MAP.md` template token. Verified
both directions before the fixes: it reported exactly the two wrong-path
references (plus the template token) and nothing else; after the fixes it
reports clean. ctxlint stays installed for root-level context files and
`ctxlint mcp` (`.mcp.json` validation); if it gains a base-path option the
house script can be retired in its favor.

## Fixes made in this pass

1. Deleted `.opencode/skills/ablate-ai-layer/scripts/__pycache__/` (SC8).
2. `.opencode/AGENTS.md`: the `wmd-verify.ps1` and `map_layer.py` references
   now point at the skills' own `scripts/` dirs.
3. `reflect/references/{divergent,judgment,tooling}-reviewer.md`: the
   skill-use scan line now names the OpenCode skill trees and the `skill`
   tool instead of the Cursor paths.
4. Added `scripts/ref-check.ps1` (the deterministic stale-reference pre-pass,
   wired as rules-check-drift step 0).
5. Wired the tools into the house surfaces: security-scan gained layer 7
   (skill supply chain, with the verdict table above in
   `references/scan-layers.md`); rules-check-drift gained the step 0
   pre-pass; authoring-a-skill step 2 and create-verification-skill step 4
   gained the agnix format check; `.opencode/AGENTS.md` documents all three.

## Re-run commands

```powershell
# skill supply chain (project + global trees, static only)
& "C:\Users\tobia\.local\bin\skillspector.exe" scan .opencode/skills --no-llm
& "C:\Users\tobia\.local\bin\skillspector.exe" scan C:\Users\tobia\.config\opencode\skills --no-llm
# config + skill shape
agnix .
# deterministic stale references in the house docs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ref-check.ps1
```

Cadence: skillspector + agnix when a skill is installed, ported, or
upstream-synced (security-scan layer 7 for the gate run); ref-check any time,
it is the rules-check-drift pre-pass.