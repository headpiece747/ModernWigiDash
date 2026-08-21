
namespace ModernWigiDash.Widgets;

/// <summary>
/// The artwork background-color rule: given the 32×32 downsample of the
/// album art, bucket the quantized colors, prefer the most colorful bucket
/// (saturation × brightness × population), and darken anything too bright.
/// Pure over the bitmap, so the color math is testable without artwork
/// loading.
/// </summary>
internal static class ArtworkBackgroundColor
{
    /// <summary>Selects the background color from a 32×32 sample. An
    /// all-dark sample (no bucket survives the brightness filter) falls back
    /// to the sample's center pixel.</summary>
    public static SKColor FromSample(SKBitmap sample)
    {
        Dictionary<int, (SKColor color, int count, float brightness)> buckets = [];

        for (int y = 0; y < sample.Height; y++)
        {
            for (int x = 0; x < sample.Width; x++)
            {
                SKColor px = sample.GetPixel(x, y);
                float max = Math.Max(Math.Max(px.Red, px.Green), px.Blue);
                float brightness = max / 255f;

                if (brightness < 0.10f || brightness > 0.92f) continue;

                int qR = (px.Red / 16) * 16;
                int qG = (px.Green / 16) * 16;
                int qB = (px.Blue / 16) * 16;
                int key = (qR << 16) | (qG << 8) | qB;

                if (buckets.TryGetValue(key, out var existing))
                {
                    if (brightness > existing.brightness)
                        buckets[key] = (px, existing.count + 1, brightness);
                    else
                        buckets[key] = (existing.color, existing.count + 1, existing.brightness);
                }
                else
                {
                    buckets[key] = (px, 1, brightness);
                }
            }
        }

        if (buckets.Count == 0)
        {
            return sample.GetPixel(16, 16);
        }

        var colorful = buckets.Values
            .Where(b =>
            {
                float min = Math.Min(Math.Min(b.color.Red, b.color.Green), b.color.Blue);
                float max = Math.Max(Math.Max(b.color.Red, b.color.Green), b.color.Blue);
                float sat = max > 0 ? (max - min) / max : 0;
                float br = max / 255f;
                return sat >= 0.22f && br >= 0.18f && br <= 0.85f;
            })
            .ToList();

        SKColor selected;
        if (colorful.Count > 0)
        {
            selected = colorful
                .OrderByDescending(b =>
                {
                    float min = Math.Min(Math.Min(b.color.Red, b.color.Green), b.color.Blue);
                    float max = Math.Max(Math.Max(b.color.Red, b.color.Green), b.color.Blue);
                    float sat = max > 0 ? (max - min) / max : 0;
                    float br = max / 255f;
                    return br * (0.5f + 0.5f * sat) * (1.0f + Math.Min(0.5f, b.count / 50.0f));
                })
                .First()
                .color;
        }
        else
        {
            selected = buckets.Values
                .OrderByDescending(b => b.brightness)
                .ThenByDescending(b => b.count)
                .First()
                .color;
        }

        float selBright = Math.Max(Math.Max(selected.Red, selected.Green), selected.Blue) / 255f;
        if (selBright > 0.65f)
        {
            float factor = 0.65f / selBright;
            selected = new SKColor(
                (byte)Math.Clamp(selected.Red * factor, 0, 255),
                (byte)Math.Clamp(selected.Green * factor, 0, 255),
                (byte)Math.Clamp(selected.Blue * factor, 0, 255));
        }

        return selected;
    }
}
