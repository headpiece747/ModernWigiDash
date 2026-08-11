using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Bounded LRU memoization of word-wrap results. The key is (text, fontSize,
/// width) with the tolerance semantics the text-label widget used historically —
/// the wrap loop measures every candidate, so per-frame recomputation is wasted
/// work at 30 FPS. A widget owns one instance for its lifetime. The cache keeps
/// the most recently used texts and evicts the least recently used entry at
/// capacity, so a renderer that wraps N messages per frame (the Twitch chat
/// stream) hits the cache for every visible message instead of re-wrapping
/// each one — while a single-message renderer (the text label) is unaffected.
/// </summary>
internal sealed class WrapCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byText = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private readonly int _capacity;

    private sealed record Entry(string Text, float FontSize, float Width, IReadOnlyList<string> Wrapped);

    /// <param name="capacity">Maximum distinct texts held; the least recently
    /// used entry is evicted when a miss lands past it.</param>
    public WrapCache(int capacity = 16)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>
    /// Returns the cached wrapped lines for (text, fontSize, width), wrapping
    /// on a miss. Text is split on newlines first, then each line is greedily
    /// word-wrapped within <paramref name="width"/> measured with
    /// <paramref name="font"/> — mirroring the widget render path exactly.
    /// </summary>
    public IReadOnlyList<string> GetOrWrap(string text, SKFont font, float fontSize, float width)
    {
        lock (_gate)
        {
            if (_byText.TryGetValue(Normalize(text), out var node)
                && Math.Abs(fontSize - node.Value.FontSize) <= 0.01f
                && Math.Abs(width - node.Value.Width) <= 0.5f)
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Wrapped;
            }

            string source = string.IsNullOrEmpty(text) ? "" : text;
            List<string> wrapped = [];
            foreach (string rawLine in source.Split('\n'))
            {
                wrapped.AddRange(TextRenderHelper.WrapText(rawLine, font, width));
            }

            var entry = new Entry(Normalize(text), fontSize, width, wrapped);
            var freshNode = new LinkedListNode<Entry>(entry);
            if (_byText.TryGetValue(entry.Text, out var staleNode))
            {
                _order.Remove(staleNode);
            }
            _byText[entry.Text] = freshNode;
            _order.AddFirst(freshNode);

            while (_order.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _byText.Remove(oldest.Value.Text);
            }
            return wrapped;
        }
    }

    private static string Normalize(string? text) => text ?? "";
}
