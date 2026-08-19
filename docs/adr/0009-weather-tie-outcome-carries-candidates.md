# ADR-0009: The weather tie outcome carries its candidates

**Date:** 2026-08-19
**Status:** Accepted
**Deciders:** Project owner

## Context

The weather cluster refuses to guess coordinates for a genuine same-name
tie — the ambiguity gate (`WeatherLocationResolver.Ambiguous`) is load-bearing;
it is what keeps a bare "Berlin" from resolving to a US Berlin. But the
tie's verdict collapsed into the fetch outcome's bare `Failed` arm:
`FetchCurrentAsync`'s no-coordinates branch returned
`WeatherFetchResult.Failed` for BOTH a failed geocode and a tie. `Failed`
carries nothing, so the flow's `Skipped` verdict kept every previous state
silently — and the widget's Location Match dropdown (populated only from an
applied `Fetched` outcome or the boot cache load) stayed **empty**.

The resolver held the tied candidates (the client twin even stored them —
`SetCandidates` + `ClearCoordinates` on the `Ambiguous` leg) and the
throttle was stamped correctly; the only thing missing was a result shape
that carried the candidates across the client → flow → widget boundary. The
user's only escape from a tied city was typing a suffix or a country code by
hand; the dropdown the property description promises ("Pick the exact place
when the city name is ambiguous") was exactly the thing a tie could never
fill.

Carrying the candidates across a snapshot-less outcome required a decision
about the `WeatherFetchResult` shape — it was flagged as "a pending ADR,
not today's contract" in CONTEXT.md. This is that decision.

## Decision

**A tie is a first-class fetch outcome: `WeatherFetchResult.Tie(
IReadOnlyList<GeocodeCandidate> Candidates, string QueryKey)`.**

- The outcome union's "a result with no snapshot is unrepresentable" rule
  generalizes: the snapshot exists only on `Fetched`; the TIED CANDIDATES
  exist only on `Tie`; a bare `Failed` (no candidates, no snapshot) remains
  the failed-geocode / no-match verdict. The client's no-coordinates branch
  returns `Tie` when the resolution outcome is `Ambiguous` with a
  non-empty candidate set (an empty-candidate tie is unresolvable anyway and
  stays `Failed`), and the ADR-0006 `Stale` verdict for an in-flight
  identity change still takes precedence.
- `Tie` carries the same `QueryKey` contract as `Fetched`/`Stale` — the
  resolution identity it was resolved for — so the flow's carried-key gate
  (now covering both carried-key outcomes, still dropping through the
  single `WeatherQueryKey.SameKey` predicate) applies to it unchanged, and
  so a tie's candidates can never leak into a different identity's
  dropdown.
- The flow gains a named verdict, `WeatherFetchFlowOutcome.AppliedTie`, and
  a named apply payload beside `TryApply`: the host seam's
  `TryApplyTie(IReadOnlyList<GeocodeCandidate> candidates, Func<bool>
  identityGuard)` (ADR-0008's rule — a new payload field is a named
  addition, never a positional change). The apply is identity-guarded under
  the host's gate exactly like the snapshot apply: an edit that changed the
  resolution inputs since the fetch wins.
- The host's tie apply is one atomic step under the gate: the data state
  RESETS TO ITS PLACEHOLDER (a tie has no data — a previous city's scalars
  must never render under the tie's header) with the data version bumped so
  the render model rebuilds, and the resolved identity takes the tied
  candidates (the dropdown), the QUERIED NAME as the honest header (there
  is no winner to name), and a cleared population. No label write-back:
  there is no resolved city to persist.
- The inspector-refresh stamp ride-through is shared: a tie's tied options
  are a candidate-set change, so an already-open inspector refreshes and
  shows the dropdown.

The escape route then rides the EXISTING pick path with zero new machinery:
a pick edits `LocationMatch`, the `Coordinates` invalidation keeps the
candidates the pick was offered from (both twins), and the geocoder's
zero-HTTP pick fast path resolves the picked candidate's coordinates and
fetches its weather.

## Consequences

**Positive:**
- The Location Match dropdown is populated on every tie the geocoder can
  name — the property's documented escape route works for the very case it
  exists for, on the first fetch.
- A tie is assertable at every module boundary without a widget instance or
  a render tick: the client's `Tie` (candidates + key + no forecast
  request), the flow's `AppliedTie` (gated apply, placeholder reset,
  drop-gate routing via the carried-key and the post-await re-check), the
  widget's dropdown population, and the tie → pick → weather journey with
  exactly one geocode.
- The tie cools down like a success (the geocode leg's throttle stamp is
  unchanged) — a tied city never re-geocodes at frame rate.
- The display can never show a previous city's scalars under a tied
  header: the placeholder reset is part of the apply.

**Negative:**
- One more outcome arm and one more host-seam method — each is a named
  addition to a union and an interface whose whole purpose is named
  extension, and the alternative (an empty dropdown on the cluster's
  primary disambiguation case) was the defect this ADR closes.
- The pane still renders its default placeholder scalars while tied (the
  pre-existing "never fetched" display); a richer no-data/ambiguous view is
  a presentation follow-up, not part of this decision.

## Alternatives considered

1. **Leave the tie as `Failed`** — the status quo. The dropdown stayed
   empty on the cluster's primary disambiguation case, and a tied city's
   only escape was hand-typed suffixes; the candidates sat in the client
   twin where no display surface could reach them.
2. **A guidance hint only** (a "found several Berlin — add a country"
   string in the pane) — small and seam-free, but the pick list stayed
   empty: the user was told a pick existed and had nowhere to make it.
3. **Carry the candidates on the existing `Failed` arm** (optional
   candidates field) — it would overload one verdict with two meanings
   ("nothing resolved" vs. "several resolved, none chosen"), and a `Failed`
   with candidates would be indistinguishable in the flow from a `Failed`
   without them only by a null check — exactly the shape the union exists
   to make unrepresentable.

## Date

2026-08-19