using System.Globalization;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Outcome of one debounced search tick, decided by <see cref="LocationSearchModel.RunSearchTickAsync"/>:
/// <see cref="LocationSearchTick.NoSearch"/> when the query is too short to
/// search, <see cref="LocationSearchTick.Stale"/> when a newer tick superseded
/// this response (the caller must not touch the UI), and
/// <see cref="LocationSearchTick.Success"/> with the candidates.
/// </summary>
internal enum LocationSearchTick
{
    NoSearch,
    Stale,
    Success
}

/// <summary>
/// Monotonic version token for one search editor: bumped before every tick
/// decides whether to search, so any response still in flight from an
/// earlier tick — including one whose query was too short — is discarded.
/// </summary>
internal sealed class SearchVersionToken
{
    public int Value { get; private set; }

    public int Next() => ++Value;
}

/// <summary>
/// The Location-search editor's decision rules — the seed/commit/query
/// behavior of the inspector's search box as data, testable without WPF.
/// The box seeds from the Location label plus the last resolved population's
/// compact suffix ("New York, New York, United States · 8.4M") — the same
/// shared formatter the search list's lines use, so the field and the list
/// can never disagree about a population. The suffix is display-only: it
/// searches and commits as the base label, so a seeded box never degrades the
/// next resolution to a bare-name tie; a real user edit takes over verbatim.
/// </summary>
internal static class LocationSearchModel
{
    /// <summary>
    /// The text the box seeds with: the base label, plus the last resolved
    /// population's compact suffix when one is known. An empty label never
    /// seeds a bare suffix.
    /// </summary>
    public static string SeedText(string baseLabel, double? population)
    {
        if (baseLabel.Length == 0 || population is not > 0)
        {
            return baseLabel;
        }

        return $"{baseLabel} · {FormatPopulation(population.Value)}";
    }

    /// <summary>
    /// The search query for one tick: while the box still holds the seeded
    /// text (no real user edit yet) the base label is searched — the
    /// population suffix matches no geocoder result. Anything else searches
    /// as typed.
    /// </summary>
    public static string QueryFor(string boxText, string seed, string baseLabel)
        => string.Equals(boxText, seed, StringComparison.Ordinal) ? baseLabel : boxText.Trim();

    /// <summary>
    /// The text a pick-less commit (Enter or focus loss) writes: the base
    /// label while the box still holds the seeded text — committing the
    /// seeded text verbatim would persist "label · 9.4k", and the next
    /// resolution's suffix component would match no candidate. A real user
    /// edit commits verbatim.
    /// </summary>
    public static string CommitText(string boxText, string seed, string baseLabel)
        => string.Equals(boxText, seed, StringComparison.Ordinal) ? baseLabel : boxText;

    /// <summary>
    /// Whether a focus loss should commit the typed text: a pick in progress
    /// inside the results popup — a mouse press there, or keyboard navigation
    /// with the popup open — vetoes the commit, which would otherwise
    /// overwrite the pending pick and close the popup mid-gesture.
    /// </summary>
    public static bool ShouldCommitOnLostFocus(bool popupPressed, bool popupOpenAndMouseOver)
        => !popupPressed && !popupOpenAndMouseOver;

    /// <summary>
    /// One debounced search tick's decision (pure — no UI). The version token
    /// is bumped BEFORE the length check, so a tick that skips the search
    /// still invalidates any response in flight from an earlier query — the
    /// popup must never reopen with results for a query the box no longer
    /// contains. Stale responses (a newer tick bumped the version while the
    /// search was in flight) are discarded.
    /// </summary>
    public static async Task<(LocationSearchTick Outcome, IReadOnlyList<GeocodeCandidate>? Candidates)> RunSearchTickAsync(
        IWidgetLocationSearch search, string query, SearchVersionToken version)
    {
        int current = version.Next();
        string trimmed = query.Trim();
        if (trimmed.Length < 2) return (LocationSearchTick.NoSearch, null);
        var candidates = await search.SearchAsync(trimmed, CancellationToken.None);
        if (current != version.Value) return (LocationSearchTick.Stale, null);
        return (LocationSearchTick.Success, candidates);
    }

    /// <summary>
    /// The one compact population format shared by the search list's
    /// candidate lines and the Location box's seed suffix: "9.4k" / "8.4M",
    /// bare number below 1000, invariant culture — one spelling, drift
    /// impossible.
    /// </summary>
    public static string FormatPopulation(double population)
        => population switch
        {
            >= 1_000_000 => $"{(population / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture)}M",
            >= 1_000 => $"{(population / 1_000).ToString("0.#", CultureInfo.InvariantCulture)}k",
            _ => population.ToString("0", CultureInfo.InvariantCulture)
        };
}
