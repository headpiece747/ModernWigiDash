namespace ModernWigiDash.Widgets;

/// <summary>
/// The outcome of decoding one media file: an animated frame set (frames +
/// per-frame durations in ms), a single still bitmap, or nothing. The
/// decoder never throws for malformed media: a refusal or a decode failure
/// is <see cref="None"/> with every partially decoded bitmap already
/// disposed, so a result is either installable or empty.
/// </summary>
/// <param name="Frames">The decoded animation frames (multi-frame media).
/// Never a length-1 array: the decoder routes single-frame media to the
/// still path, so this is null or has 2+ elements.</param>
/// <param name="Durations">Per-frame durations in ms (parallel to
/// <paramref name="Frames"/>).</param>
/// <param name="Still">The decoded still image (single-frame media).</param>
internal sealed record MediaDecodeResult(
    SKBitmap[]? Frames,
    long[]? Durations,
    SKBitmap? Still)
{
    /// <summary>The empty outcome: nothing to install.</summary>
    public static readonly MediaDecodeResult None = new(null, null, null);

    /// <summary>True when the decode produced a multi-frame animation.
    /// <see cref="Frames"/> is never a length-1 array: single-frame media
    /// decodes as a <see cref="Still"/>.</summary>
    public bool IsAnimated => Frames is { Length: > 1 };

    /// <summary>True when the decode produced a single still image.</summary>
    public bool IsStill => Still is not null;

    /// <summary>
    /// Disposes every bitmap in the result — a caller that discards decoded
    /// media (version token stale, path changed) must not leak its buffers.
    /// Safe on <see cref="None"/>.
    /// </summary>
    public void DisposeMedia()
    {
        if (Frames is not null)
        {
            foreach (var frame in Frames)
            {
                frame?.Dispose();
            }
        }
        Still?.Dispose();
    }
}
