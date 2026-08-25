### Runtime forensics

**You own the diagnosis. Instrument the live process, don't theorize from source.** For "why is X leaking / spinning / slow at runtime", heap snapshots, idle-but-busy processes, intermittent glitches. The deliverable is a cited diagnosis, not a fix.

1. Capture the live signal on the matching surface: process symptoms via GliderTrace (`trace_start` with `collectTrace`, `trace_attach`, counters via `collectCounters`), UI-driven symptoms via the `verify-modernwigidash` harness. A CPU profile for a spinning process, a heap or GC dump for a leak, a nettrace for a frame-path glitch. A real artifact, not a guess.
2. Reduce the artifact to the smoking gun: the function on the hot path, the retainer chain from the leaked object to a GC root, the loop firing without input. Parse large artifacts in a subagent (the **principle-guard-the-context-window** principle skill), keep the reduced finding in the main thread.
3. Prove the mechanism before believing it. Confirm the hypothesis cheaply against the live process: a second targeted capture (a counter that moves only under the suspect, a short `trace_attach` window on the symptom), or a temporary log line at the suspect site through the existing log seam, removed before the commit. A plausible-but-unconfirmed cause can be wrong while the real one sits one layer over.
4. Map the finding back to source: file, symbol, the line that allocates or schedules.
5. Throughput checkpoint stays one line: `throughput checkpoint: n/a, read-only forensics`.

**Reply:** the signal captured, the reduced finding, how you proved the mechanism, the source location, artifact paths. No fix unless asked; hand back to Bug fix or Perf once the cause is known.