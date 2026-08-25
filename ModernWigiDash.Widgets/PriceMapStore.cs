using System.Collections.Concurrent;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The price-map store seam (Widgets): the one owner of the shared price map
/// and of every write into it. Two named merge rules, one clock:
/// <see cref="ApplyLive"/> (a live source always overwrites the price and the
/// timestamp; a sample without a change figure keeps the previously known
/// change instead of zeroing it) and <see cref="ApplyFallback"/> (a fallback
/// sample writes only past the <see cref="ShouldKeepExisting"/> downgrade
/// guard, with the same change-keep). The legs are pure fetchers and the
/// WebSocket loops route their parses here, so every store site is a rule
/// choice at a named entry point, never a re-derivation of the merge
/// behaviour at the call site.
/// </summary>
internal sealed class PriceMapStore(Func<DateTime> now)
{
    private readonly ConcurrentDictionary<string, PriceInfo> _prices = new();

    /// <summary>The read seam: the record for a feed key, or null when the
    /// key was never stored (the widget render tick's one access to the
    /// map).</summary>
    internal PriceInfo? TryGet(string key)
        => _prices.TryGetValue(key, out var info) ? info : null;

    /// <summary>The release seam: a fully-unsubscribed symbol's record is
    /// stale by construction and leaves the map (the manager's
    /// <c>Unsubscribe</c>).</summary>
    internal bool TryRemove(string key)
        => _prices.TryRemove(key, out _);

    /// <summary>The live rule: the source's sample always wins the price and
    /// the timestamp, from any source, at any age (a live feed is the fresh
    /// data; nothing protects a record from it). A sample without a change
    /// figure (a Finnhub trade, a quote missing its day change) keeps the
    /// previously known change instead of zeroing it.</summary>
    internal void ApplyLive(string key, decimal price, decimal? change, string source, string currencySymbol = "$")
    {
        _prices.AddOrUpdate(key,
            _ => NewPrice(price, change, null, source, currencySymbol),
            (_, existing) => NewPrice(price, change, existing, source, currencySymbol));
    }

    /// <summary>The fallback rule: a fallback sample (the one-shot seed, the
    /// crypto cycle's batch tail) writes only past the downgrade guard, with
    /// the same change-keep as the live rule.</summary>
    internal void ApplyFallback(string key, decimal price, decimal? change, string source, string currencySymbol = "$")
    {
        _prices.AddOrUpdate(key,
            _ => NewPrice(price, change, null, source, currencySymbol),
            (_, existing) => ShouldKeepExisting(existing, source, now())
                ? existing
                : NewPrice(price, change, existing, source, currencySymbol));
    }

    /// <summary>The fallback downgrade guard, the one spelling the fallback
    /// rule applies before writing: a fresh record from any OTHER source is
    /// kept, because live feed data is never downgraded by the fallback's
    /// slower cadence and coarser data. A same-source refresh and a stale
    /// record are replaced. Pure over the existing record, the incoming
    /// source, and the clock so the rule is directly testable without the
    /// price map.</summary>
    internal static bool ShouldKeepExisting(PriceInfo existing, string incomingSource, DateTime now)
        => !string.Equals(existing.Source, incomingSource, StringComparison.Ordinal)
            && (now - existing.Timestamp).TotalSeconds < PriceInfo.FreshnessSeconds;

    /// <summary>Builds a fresh price record: the timestamp is the store's
    /// clock read at write time, the source labels the record, and a null
    /// incoming change falls back to the existing record's change (zero when
    /// there is none) — the one change resolution shared by both rules.</summary>
    private PriceInfo NewPrice(decimal price, decimal? change, PriceInfo? existing, string source, string currencySymbol)
        => new()
        {
            Price = price,
            ChangePercent = change ?? existing?.ChangePercent ?? 0m,
            Source = source,
            Timestamp = now(),
            CurrencySymbol = currencySymbol
        };
}
