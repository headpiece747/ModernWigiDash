using System.Collections.Concurrent;

namespace ModernWigiDash.Sdk;

/// <summary>
/// One parsed-path cache rule for the two render stacks: case-insensitive
/// keys, parse once per key, null-safe (a parser that returns null — unknown
/// or invalid input — caches the miss, so the fallback policy runs once).
/// WPF's <c>GriddyIconGeometry</c> and the Skia <c>SvgPathCache</c> each supply
/// their own parser and map the null result to their stack's fallback (null
/// vs empty path) — keying and parse-once are declared here, not twice.
/// </summary>
// S2743: the per-close-constructed-type cache is the design — each render
// stack (WPF Geometry, Skia SKPath) owns its own key space, and a shared
// cache would conflate the two namespaces.
#pragma warning disable S2743
public static class SvgPathParseCache<T>
{
    private sealed class Box
    {
        public static readonly Box Miss = new(false, default);

        public Box(T value) : this(true, value) { }

        private Box(bool hasValue, T? value)
        {
            HasValue = hasValue;
            Value = value;
        }

        public bool HasValue { get; }
        public T? Value { get; }
    }

    private static readonly ConcurrentDictionary<string, Box> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached parsed value for <paramref name="key"/>, invoking
    /// <paramref name="parse"/> once on first use (even when it returns null —
    /// the miss is cached, so an unknown key never re-parses per frame).
    /// </summary>
    public static T? GetOrParse(string key, Func<T?> parse)
        => Cache.GetOrAdd(key, _ => parse() is { } value ? new Box(value) : Box.Miss).Value;
}
#pragma warning restore S2743
