namespace ModernWigiDash.Widgets;

/// <summary>
/// One number: how long the display will wait on the network before it calls a
/// fetch a failure. Every outbound HTTP leg in this project budgets from here —
/// the weather client and its geocoder, both calendar-feed legs, and the Twitch
/// API client.
///
/// It used to be spelled 30 s in five places, in three files, and nothing tied
/// them together: tightening the weather client's header phase left the geocoder
/// reading the body for another half minute, and a future edit to any one of the
/// five would have silently disagreed with the other four. Nothing in the code
/// or the glossary said the repetition was deliberate (found by the architecture
/// review, 2026-09-27).
///
/// The two shapes a caller can spend it in, because they are not the same
/// promise and a reader should not have to guess which one it is looking at:
///
/// <list type="bullet">
/// <item>ONE WAIT for the whole leg — a client whose own <c>Timeout</c> covers
/// the response, or a leg that wraps send-and-read in a single linked CTS. The
/// calendar legs and the Twitch API client are this shape.</item>
/// <item>ONE WAIT PER PHASE, so a worst case of two — the weather cluster,
/// because under <c>ResponseHeadersRead</c> the shared client's <c>Timeout</c>
/// bounds only the header phase and the streamed body needs its own deadline
/// (a slow-drip server would otherwise hold the leg indefinitely). The
/// geocoder's second bound is deliberate and correct; what was missing was the
/// statement that the two are phases of one budget rather than two independent
/// numbers that happen to agree.</item>
/// </list>
///
/// A caller that wants a tighter bound is not fighting this constant: every leg
/// above already takes a per-instance timeout override for exactly that.
/// </summary>
internal static class NetworkBudget
{
    /// <summary>The wait. See the type doc for the two shapes it is spent in.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
}
