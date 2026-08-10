using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Single-slot memoization of the word-wrap result for one widget. The key is
/// (text, fontSize, width) with the tolerance semantics the text-label widget
/// used historically — the wrap loop measures every candidate, so per-frame
/// recomputation is wasted work at 30 FPS. A widget owns one instance for its
/// lifetime; the last rendered text is the only entry the slot keeps, so the
/// cache cannot grow with property changes.
/// </summary>
internal sealed class WrapCache
{
    private readonly Lock _gate = new();
    private string _keyText = "";
    private float _keyFontSize = -1f;
    private float _keyWidth = -1f;
    private IReadOnlyList<string> _wrapped = [];

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
            if (text == _keyText
                && Math.Abs(fontSize - _keyFontSize) <= 0.01f
                && Math.Abs(width - _keyWidth) <= 0.5f)
            {
                return _wrapped;
            }

            string source = string.IsNullOrEmpty(text) ? "" : text;
            List<string> wrapped = [];
            foreach (string rawLine in source.Split('\n'))
            {
                wrapped.AddRange(TextRenderHelper.WrapText(rawLine, font, width));
            }

            _keyText = text;
            _keyFontSize = fontSize;
            _keyWidth = width;
            _wrapped = wrapped;
            return wrapped;
        }
    }
}
