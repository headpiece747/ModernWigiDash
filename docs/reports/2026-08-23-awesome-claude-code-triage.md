# awesome-claude-code mining pass (2026-08-23)

Source: `THE_RESOURCES_TABLE_NEW.csv` from
https://github.com/hesreallyhim/awesome-claude-code (157 entries,
fetched 2026-08-23). Method: every row triaged against this repo's
shape (a .NET 10 WPF desktop app, USB HID hardware, no web surface, an
OpenCode harness with a project and a global skill layer, the
Glider/GliderTrace/CodeGraph MCP set, and the CONTEXT.md + ADR + gate
layer). Nothing is installed. This is a triage record for a sign-off
decision, not an adoption.

## Verdict in one line

Most of the list targets the Claude Code harness (statuslines, session
monitors, GUI clients, remote and voice control, web design taste, VM
sandboxes, CI actions). Roughly a third is Claude-Code-specific or out
of shape for a local WPF/USB desktop app, and a fifth overlaps what
this repo already encodes (unslop, the superpowers tree, the
verification skills, the orchestration pack, the security-scan skill).
Five entries are real candidates.

## Shortlist (3 recommended, 2 evaluated-hold)

1. **SkillSpector** (NVIDIA) - https://github.com/NVIDIA/SkillSpector
   Security scanner for AI agent skills: detects vulnerabilities,
   malicious patterns, and security risks in skill content. The repo
   ingests 30+ skills from three external sources (Matt Pocock,
   coleam00/skills, poteto pstack) and runs skills that edit other
   skills (reflect, convention-learner, maintain-verification-skill);
   the security-scan skill's layers (packages, secrets, OWASP patterns,
   auth, data protection) do not cover the skill supply chain. Adoption
   shape: a seventh layer that scans `.opencode/skills` and the global
   skills dir on install and on upstream sync.
   Fit: high. Cost: one CLI pass over the skill trees.

2. **Ctxlint** - https://github.com/ctxlint/Ctxlint
   CLI linter for AI agent context files: stale references, dead
   commands, hardcoded secrets, modular tested rule set. This repo's
   load-bearing doc is CONTEXT.md (about 280 lines of module, symbol,
   and test names) plus the AGENTS.md files. rules-check-drift and
   second-brain-audit are LLM passes that re-read the whole doc; Ctxlint
   is the deterministic cheap half that checks the names a context file
   cites still exist in the tree. It can run before the LLM pass, or as
   a fifth gate stage if it stays fast; the reason to keep it manual
   first is gate bloat, not fit.
   Fit: high against the house's core memory-rot risk. Cost: one CLI.

3. **agnix** - https://github.com/agent-sh/agnix
   Linter for assistant instruction files: validates CLAUDE.md,
   AGENTS.md, SKILL.md, hooks, and MCP config, with autofixes. The repo
   authors skills (authoring-a-skill, create-verification-skill,
   writing-for-agents) and installs 30+; a format validator catches
   broken frontmatter and structure at authoring time, before a skill
   drifts into a run. It pairs with SkillSpector: agnix checks shape,
   SkillSpector checks safety.
   Fit: medium-high. Cost: one CLI.

4. **roampal-core** (evaluated, hold) - https://github.com/roampal-ai/roampal-core
   Outcome-based persistent-memory MCP that explicitly supports OpenCode
   (good advice promoted, bad advice demoted). The repo's Session
   Lifecycle names an agentmemory seam that applies "when that MCP is
   connected", currently dangling. roampal-core is a concrete candidate
   for exactly that seam. Held: a new MCP server against a deliberate
   three-server MCP set (Glider, GliderTrace, CodeGraph); revisit if the
   memory seam becomes load-bearing.

5. **Selvedge** (covered, no action) - https://github.com/masondelan/selvedge
   "git blame for AI agents, but for the why": captures the agent's
   reasoning live per change in local SQLite. The repo already encodes
   the why in ADRs, conventional commit bodies, and the
   show-me-your-work decision log for long runs. No gap left for it.

### Runners-up, noted and parked

- **Schliff** (https://github.com/Zandereins/schliff): deterministic
  8-dimension scorer for instruction files with anti-gaming detection,
  zero deps. Overlaps the health-check skill's shape; consider in the
  next skill-authoring session when a quality score is wanted.
- **BlockWatch** (https://github.com/mennanov/blockwatch): keeps
  co-dependent code, docs, and config in sync. Attractive for
  CONTEXT.md vs code drift, but the rule model is unproven here; Ctxlint
  covers the reference half first.
- **Upkeep** (https://github.com/wei18/Upkeep): docs/spec/asset drift
  audit with evidence, output-only. Overlaps rules-check-drift and
  second-brain-audit, which already do the job with repo knowledge
  Upkeep would have to re-derive.

## Already covered by the house layer (no action)

| List area | House counterpart |
|---|---|
| Avoid AI Writing (49+ AI-ism pattern categories) | `unslop` skill (must always apply) |
| Superpowers (obra/superpowers) | already in the repo (`.superpowers/` tree, named in the doc exclusions) |
| fable / MAMA / Callimachus / capy / Claude Mnemonic (session transcript memory) | agentmemory seam + CONTEXT.md + ADRs + handoff skill |
| presence (per-repo memory, test-evidenced success claims) | the gate trail (`.audit/gates.tsv`) + the test pins |
| instruction linting (the LLM-pass half) | rules-check-drift + second-brain-audit + ablate-ai-layer; the deterministic half is the shortlist above |
| RIPER / AB Method / Harness / Project Workflow System / Ralph Wiggum (workflow discipline) | poteto pack (sequence-verifiable-units, figure-it-out) + wayfinder + to-tickets/implement + show-me-your-work |
| Agent Collab / gstack / fable-mode / Multi-Agent Observability (orchestration) | arena, swarm, reflect, and the nine specialist subagents |
| Dev Browser (verify work through a browser) | verify-modernwigidash + hardware-e2e-validation (UIA plus the physical device; no web surface exists) |
| StyleSeed / UI Craft / Diagram Design / visual-explainer (design taste) | the ThemeSettings/ThemeManager theme system + unslop + technical-writing |
| Claude Code Safety Net / Safety Guard / GouvernAI / Node9 / Cleat (Claude Code hook guardrails) | the opencode permission rules + the gate guard (`scripts/hooks/pre-commit`) |
| aicontainer / machine / Brood Box / Incus (VM sandboxes) | local WPF dev loop; no sandbox need |
| Agent Guard (secret-leak guardrail) | dotnet-rules section 3 + LogLine redaction + the security-scan skill |
| ccusage / statuslines / session monitors / alternative clients / remote and voice / creative media / SEO / Terraform / OTEL-collector / Anthropic CI actions | out of shape: wrong harness, no web surface, local single model, no CI |

## Next step

Nothing is installed. If the user signs off on one or more of the three
recommended entries, the adoption shape per entry is: install the CLI,
run it once over the repo trees (`.opencode/`, `CONTEXT.md`, the
AGENTS.md files, the global skills dir), record the findings in `docs/`,
and wire the repeat into the existing surface (security-scan skill gains
a skill-supply-chain layer; rules-check-drift gains a deterministic
pre-pass; the skill-authoring playbook gains the format check). No
gate-stage changes in this pass: the four-stage gate is freshly green
and the gate guard polices it.