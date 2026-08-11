using Windows.Storage.Streams;
using ModernWigiDash.Core.Rendering;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Owns the Now Playing widget's artwork reload pipeline: key-change detection
/// (including the "artwork became available" transition), the load pipeline
/// (stream reopen, 10MB cap, decode), background-color extraction, and
/// retire-and-publish via <see cref="RetiredBitmapSet"/>. Snapshot updates
/// arrive from the monitor's threads; each completed load — success, skipped,
/// or failed — raises <see cref="ArtworkChanged"/> so the widget can request a
/// render at the same point the old inline pipeline did. Retired bitmaps are
/// only disposed on the render thread through <see cref="DisposeRetired"/>,
/// never from the background refresh threads that replaced them.
/// </summary>
public sealed class ArtworkLoader
{

    private static readonly SKColor DefaultBackground = new(18, 18, 24);
    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly IArtworkDecoder _decoder;
    private readonly Action<string, Exception?>? _logError;
    private readonly RetiredBitmapSet _retirement = new();

    private SKBitmap? _albumArt;
    private SKColor _bgColor = DefaultBackground;
    private string _currentArtKey = "";
    private string _loadedArtworkKey = "";
    private string _loadingArtworkKey = "";
    private int _artLoadVersion;
    private bool _disposed;

    /// <summary>
    /// Raised after the loader finishes processing a snapshot change: with the
    /// published artwork state after a completed reload (payload null when the
    /// session was lost), or with the current state for clear/no-op updates.
    /// </summary>
    public event Action<ArtworkLoaded?>? ArtworkChanged;

    public ArtworkLoader(Action<string, Exception?>? logError = null)
        : this(new WinRtArtworkDecoder(), logError)
    {
    }

    internal ArtworkLoader(IArtworkDecoder decoder, Action<string, Exception?>? logError = null)
    {
        _decoder = decoder;
        _logError = logError;
    }

    /// <summary>The artwork state last published to the renderer (never null).</summary>
    public ArtworkLoaded Current => new(_albumArt, _loadedArtworkKey, _bgColor);

    /// <summary>
    /// Reacts to a monitor snapshot update: clears the artwork pipeline when the
    /// session is lost, otherwise reloads album art when the track's art key
    /// changes or the artwork becomes available for the current key.
    /// </summary>
    public void NotifySnapshotChanged(MediaSessionUpdate? update)
    {
        if (update is null)
        {
            DisposeArtwork();
            _bgColor = DefaultBackground;
            ArtworkChanged?.Invoke(null);
            return;
        }

        bool trackChanged = update.ArtKey != _currentArtKey;
        bool artworkBecameAvailable = update.Thumbnail is not null &&
            _loadedArtworkKey != update.ArtKey && _loadingArtworkKey != update.ArtKey;
        if (trackChanged || artworkBecameAvailable)
        {
            _currentArtKey = update.ArtKey;
            _ = LoadArtworkAsync(update.Thumbnail, update.ArtKey);
        }
        else
        {
            ArtworkChanged?.Invoke(Current);
        }
    }

    /// <summary>
    /// Drains and disposes retired bitmaps. Call on the render thread at the
    /// start of a render pass, never from a background refresh.
    /// </summary>
    internal void DisposeRetired() => _retirement.DisposeRetired();

    /// <summary>Teardown: retires the current artwork and disposes everything pending.</summary>
    public void DisposeAll()
    {
        _disposed = true;
        DisposeArtwork();
        _retirement.DisposeAll();
    }

    /// <summary>The underlying retirement set (test seam: observes pending drain state).</summary>
    internal RetiredBitmapSet RetirementSet => _retirement;

    private async Task LoadArtworkAsync(IRandomAccessStreamReference? thumbnail, string artKey)
    {
        int version = ++_artLoadVersion;
        DisposeArtwork();

        if (thumbnail is null)
        {
            _bgColor = DefaultBackground;
            ArtworkChanged?.Invoke(new ArtworkLoaded(null, artKey, DefaultBackground));
            return;
        }

        _loadingArtworkKey = artKey;
        try
        {
            ArtworkDecodeResult result;
            try
            {
                result = await _decoder.DecodeAsync(thumbnail);
            }
            catch (Exception ex)
            {
                _logError?.Invoke($"Album art decode failed: {ex.Message}", ex);
                result = new ArtworkDecodeResult(null, false);
                _bgColor = DefaultBackground;
            }

            if (result.Oversized)
            {
                _logError?.Invoke("Album art skipped: thumbnail stream exceeds 10 MB", null);
                result = new ArtworkDecodeResult(null, false);
                _bgColor = DefaultBackground;
            }

            if (_disposed || version != _artLoadVersion || artKey != _currentArtKey)
            {
                result.Bitmap?.Dispose();
            }
            else
            {
                _albumArt = result.Bitmap;
                ExtractBackgroundColor();
                _loadedArtworkKey = artKey;
            }
        }
        finally
        {
            if (_loadingArtworkKey == artKey)
                _loadingArtworkKey = "";
        }

        ArtworkChanged?.Invoke(Current);
    }

    private void DisposeArtwork()
    {
        // Retire instead of disposing: SMTC refresh threads replace the
        // artwork, and the 30 FPS render thread may be inside
        // canvas.DrawBitmap(_albumArt) at this instant. Disposing the
        // native pixel memory there crashes in sk_image_new_from_bitmap
        // (0xc0000005) — disposal happens on the render thread at the next
        // Render pass (_retirement.DisposeRetired), never from a background
        // refresh.
        _retirement.Retire(_albumArt);
        _albumArt = null;
        _loadedArtworkKey = "";
    }

    private void ExtractBackgroundColor()
    {
        // Snapshot once: another SMTC refresh thread may retire the artwork
        // between the null check and the draw.
        var art = _albumArt;
        if (art is null)
        {
            _bgColor = DefaultBackground;
            return;
        }

        try
        {
            using var sample = new SKBitmap(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(sample);
            canvas.Clear();
            canvas.DrawBitmap(art, new SKRect(0, 0, 32, 32), HighQualitySampling);
            canvas.Flush();

            _bgColor = ArtworkBackgroundColor.FromSample(sample);
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Album art background color extraction failed; using default background: {ex.Message}", ex);
            _bgColor = DefaultBackground;
        }
    }
}

/// <summary>
/// A point-in-time artwork state for the renderer: the published bitmap (or
/// null when no artwork is available), the key it was loaded under, and the
/// background color extracted from it.
/// </summary>
public sealed record ArtworkLoaded(SKBitmap? Bitmap, string ArtKey, SKColor BackgroundColor);

/// <summary>Test seam over the artwork decode pipeline (stream reopen, 10MB cap, decode).</summary>
internal interface IArtworkDecoder
{
    Task<ArtworkDecodeResult> DecodeAsync(IRandomAccessStreamReference thumbnail);
}

/// <summary>Decode outcome: the bitmap, or the reason the pipeline refused to load it.</summary>
internal sealed record ArtworkDecodeResult(SKBitmap? Bitmap, bool Oversized);

/// <summary>
/// Real <see cref="IArtworkDecoder"/> over the WinRT thumbnail stream: reopens
/// the stream, enforces the 10MB size cap, reads the payload, and decodes it.
/// The widget's stale-load guards live in the loader around this call.
/// </summary>
internal sealed class WinRtArtworkDecoder : IArtworkDecoder
{
    public async Task<ArtworkDecodeResult> DecodeAsync(IRandomAccessStreamReference thumbnail)
    {
        using var stream = await thumbnail.OpenReadAsync();

        ulong size = stream.Size;
        if (size == 0 || size > 10UL * 1024 * 1024)
            return new ArtworkDecodeResult(null, Oversized: size > 10UL * 1024 * 1024);

        byte[] data = new byte[(int)size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(data);
        }

        var decoded = await Task.Run(() => SKBitmap.Decode(data));
        return new ArtworkDecodeResult(decoded, false);
    }
}
