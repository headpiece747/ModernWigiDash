using System.Collections.Concurrent;
using System.Threading;
using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Caches the installed system font family list and per-(family, style) typefaces so
/// widgets and the inspector can pick fonts without re-querying the font manager.
/// </summary>
public static class FontCatalog
{
    private static readonly Lock Gate = new();
    private static string[]? _families;
    private static readonly ConcurrentDictionary<(string Family, SKFontStyle Style), SKTypeface> TypefaceCache = new();

    public static string[] GetAllFamilies()
    {
        if (_families != null) return _families;
        lock (Gate)
        {
            if (_families == null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> list = [];
                if (seen.Add("Geist"))
                    list.Add("Geist");
                foreach (string family in SKFontManager.Default.FontFamilies)
                {
                    if (!string.IsNullOrWhiteSpace(family) && seen.Add(family))
                        list.Add(family);
                }
                _families = list.ToArray();
            }
        }
        return _families;
    }

    public static SKTypeface GetTypeface(string familyName, SKFontStyle style)
    {
        return TypefaceCache.GetOrAdd((familyName, style), key =>
        {
            try
            {
                SKTypeface? tf = SKTypeface.FromFamilyName(key.Family, key.Style);
                if (tf is { Handle: not 0 }) return tf;
            }
            catch
            {
                // Fall through to the app font below.
                System.Diagnostics.Debug.WriteLine("Typeface lookup failed, falling back to app font");
            }

            return FontHelper.GeistTypeface;
        });
    }
}
