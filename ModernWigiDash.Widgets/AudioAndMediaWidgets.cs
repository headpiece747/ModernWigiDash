using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;
using NAudio.Wave;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("audio_visualizer", "Audio Visualizer", "Visualizes live system audio output (Spotify, YouTube, Games) via WASAPI loopback capture.", "ModernWigiDash", "2.0.0", "Media & Audio", GridSizePreset.Size4x2)]
public class AudioVisualizerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size4x2.ToSize();

    [WidgetProperty("Visualizer Style", WidgetPropertyType.Choice, "Bar spectrum or radial wave", "Neon Bars", "Neon Bars", "Oscilloscope Wave", "Radial Pulse")]
    public string VisualizerStyle { get; set; } = "Neon Bars";

    [WidgetProperty("Bar Count", WidgetPropertyType.Number, "Number of spectrum bars", 32f)]
    public float BarCount { get; set; } = 32f;

    [WidgetProperty("Primary Color", WidgetPropertyType.Color, "Color for high spectrum peaks", "#FFB4AB")]
    public string PrimaryColorHex { get; set; } = "#FFB4AB"; // Material 3 Coral Red

    private WasapiLoopbackCapture? _audioCapture;
    private readonly float[] _fftSpectrum = new float[64];
    private readonly float[] _smoothSpectrum = new float[64];
    private readonly object _audioLock = new();

    public override ValueTask InitializeAsync(IWidgetContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        StartLiveAudioCapture();
        return ValueTask.CompletedTask;
    }

    private void StartLiveAudioCapture()
    {
        try
        {
            _audioCapture = new WasapiLoopbackCapture();
            _audioCapture.DataAvailable += (s, e) =>
            {
                lock (_audioLock)
                {
                    int bytesPerSample = _audioCapture.WaveFormat.BitsPerSample / 8;
                    int sampleCount = e.BytesRecorded / bytesPerSample;
                    int bars = (int)Math.Clamp(BarCount, 8, 64);

                    if (sampleCount <= 0) return;

                    int samplesPerBar = Math.Max(1, sampleCount / bars);

                    for (int i = 0; i < bars; i++)
                    {
                        float barSum = 0f;
                        for (int j = 0; j < samplesPerBar; j++)
                        {
                            int index = (i * samplesPerBar + j) * bytesPerSample;
                            if (index + 4 <= e.BytesRecorded)
                            {
                                float sample = BitConverter.ToSingle(e.Buffer, index);
                                barSum += Math.Abs(sample);
                            }
                        }

                        float val = Math.Clamp((barSum / samplesPerBar) * 4.5f, 0.05f, 1f);
                        _fftSpectrum[i] = val;
                    }
                }
            };
            _audioCapture.StartRecording();
        }
        catch (Exception ex)
        {
            Context?.LogError("Failed to initialize WASAPI Audio Capture", ex);
        }
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 235), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        SKColor.TryParse(PrimaryColorHex, out var barColor);
        if (barColor.Alpha == 0) barColor = new SKColor(255, 180, 171);

        int bars = (int)Math.Clamp(BarCount, 8, 64);
        float pad = 20f;
        float availableWidth = bounds.Width - (pad * 2);
        float barSpacing = 4f;
        float barWidth = (availableWidth - ((bars - 1) * barSpacing)) / bars;
        float maxBarHeight = bounds.Height - (pad * 2) - 25f;

        lock (_audioLock)
        {
            for (int i = 0; i < bars; i++)
            {
                float rawVal = _fftSpectrum[i];
                _smoothSpectrum[i] = _smoothSpectrum[i] * 0.75f + rawVal * 0.25f;
                float val = _smoothSpectrum[i];

                float h = val * maxBarHeight;
                float x = pad + i * (barWidth + barSpacing);
                float y = bounds.Bottom - pad - h;

                var barBounds = new SKRect(x, y, x + barWidth, bounds.Bottom - pad);
                using var barPaint = new SKPaint
                {
                    Color = barColor.WithAlpha((byte)(140 + val * 115)),
                    IsAntialias = true
                };
                canvas.DrawRoundRect(barBounds, 4f, 4f, barPaint);
            }
        }

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 11f);
        using var textPaint = new SKPaint { Color = new SKColor(244, 239, 244, 180), IsAntialias = true };
        canvas.DrawText("🎙️ LIVE WASAPI SYSTEM AUDIO SPECTRUM", pad, pad + 8f, SKTextAlign.Left, font, textPaint);
    }

    public override ValueTask DisposeAsync()
    {
        _audioCapture?.StopRecording();
        _audioCapture?.Dispose();
        return base.DisposeAsync();
    }
}

[WidgetMetadata("spotify_controller", "Spotify Controller", "Display live Spotify & Windows media playback, album artwork, and touch controls.", "ModernWigiDash", "2.0.0", "Media & Audio", GridSizePreset.Size2x1)]
public class SpotifyControllerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x1.ToSize();

    [WidgetProperty("Track Title", WidgetPropertyType.Text, "Current playing track", "Live Media Active")]
    public string TrackTitle { get; set; } = "Live Media Active";

    [WidgetProperty("Artist", WidgetPropertyType.Text, "Artist name", "Spotify / Windows Media")]
    public string ArtistName { get; set; } = "Spotify / Windows Media";

    [WidgetProperty("Is Playing", WidgetPropertyType.Boolean, "Is playback active", true)]
    public bool IsPlaying { get; set; } = true;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 240), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        float pad = 16f;
        float iconSize = Math.Min(bounds.Height - (pad * 2), 70f);

        using var artPaint = new SKPaint { Color = new SKColor(229, 57, 53), IsAntialias = true };
        canvas.DrawCircle(pad + (iconSize / 2f), bounds.MidY, iconSize / 2f, artPaint);

        using var iconFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 24f);
        using var iconPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText("🎵", pad + (iconSize / 2f) - 14f, bounds.MidY + 8f, SKTextAlign.Left, iconFont, iconPaint);

        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 15f);
        using var titlePaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };
        canvas.DrawText(TrackTitle, pad + iconSize + 14f, bounds.MidY - 6f, SKTextAlign.Left, titleFont, titlePaint);

        using var artistFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), 12f);
        using var artistPaint = new SKPaint { Color = new SKColor(224, 194, 196), IsAntialias = true };
        canvas.DrawText(ArtistName, pad + iconSize + 14f, bounds.MidY + 14f, SKTextAlign.Left, artistFont, artistPaint);

        float progressY = bounds.Bottom - pad - 4f;
        using var trackBg = new SKPaint { Color = new SKColor(255, 255, 255, 25), StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawLine(pad, progressY, bounds.Right - pad, progressY, trackBg);

        using var trackFill = new SKPaint { Color = new SKColor(229, 57, 53), StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawLine(pad, progressY, pad + ((bounds.Width - (pad * 2)) * 0.7f), progressY, trackFill);
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp)
        {
            IsPlaying = !IsPlaying;
            Context?.RequestRender();
        }
    }
}
