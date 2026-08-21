### Eval

**You own the experiment design. Plan, blind, run, synthesize.**

Evals test how a change affects agent behavior before promoting it: a new skill variant, a structural change, a prompt tweak. The failure mode is the observer effect. An agent that knows it's being evaluated behaves differently, so candidates must run blind.

**Non-negotiables for blinding:**

- No `eval`, `test`, `judge`, `experiment`, `rubric`, `score`, `compare`, `benchmark`, `candidate`, or `arena` in any directory, file, or prompt the candidate sees.
- The candidate prompt looks like an organic user request. State the goal, not the meta. "build me a small todo cli" not "show me how you follow the principles chain".
- No chain-eliciting cues. Don't ask the candidate to list which skills, principles, or files they applied; that meta-prompt inflates citation behavior. Ask for design notes generally and grade chain-following from code shape, not self-report.
- Sanitize directory and slug names. Use project-shaped names a user might pick, not labels like `candidate-1` or `agent-a`.
- Don't tell the candidate other candidates exist.
- The judge can know it's judging but sees outputs by sanitized label only, never by which candidate produced them.
- Comparing two variants: one judge scores both sets in a single pass on one scale, blind to which set each came from. Two judge runs with different prompts don't compare, the calibration drifts.

**Steps:**

1. **Frame.** State what variant is under test and what behavior counts as success. Write the rubric (3-6 concrete criteria) for the judge only. Hold it back from candidates.
2. **Set up sanitized environments.** Per-candidate working dir with the variant in place. Plant any context an organic task would have: a project skeleton, the skills the candidate would naturally read.
3. **Author one organic prompt.** What a user would type. No leakage of what's being measured.
4. **Spawn N parallel candidates** per the **arena** skill's Phase B. One local model here, so the variance comes from fresh contexts and (where useful) deliberately different brief framings, not from different models. Each works in its own sanitized dir; same prompt to each.
5. **Spawn one blinded judge** as a fresh subagent per the **arena** skill's Phase C. A different model family is not available on this host; the judge's independent context is the diversity edge. Judge sees outputs by sanitized label and the rubric, never which candidate produced them.
6. **Verify the chain from the run record, not self-report.** Grade from what each candidate actually did: the files it opened, the tests it ran, the trail it left (a show-me-your-work log when one exists), the shape of the code it produced. Citing a principle is not reading its leaf skill, and reading it is not applying it.
7. **Read every candidate output yourself** end to end. Compare to the judge's verdict. Disagreement means one of you is biased or the rubric is ambiguous. Synthesize.

**Reply:** variant under test, rubric, per-candidate notes, judge's verdict, your synthesis, and a recommendation for whether to promote the variant.