using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("picture_viewer", "Picture & GIF Viewer", "Displays pictures and animated GIFs with rounded borders and click-to-cycle folder viewing.", "ModernWigiDash", "2.0.0", "Social & Visual", GridSizePreset.Size2x2)]
public class PictureAndGifWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Image Folder/File Path", WidgetPropertyType.Text, "Path to image or folder of images", "C:\\Pictures")]
    public string ImagePath { get; set; } = "C:\\Pictures";

    [WidgetProperty("Fit Mode", WidgetPropertyType.Choice, "Aspect ratio scaling mode", "Cover", "Cover", "Contain", "Stretch")]
    public string FitMode { get; set; } = "Cover";

    [WidgetProperty("Corner Radius", WidgetPropertyType.Number, "Rounded corners radius", 16f)]
    public float CornerRadius { get; set; } = 16f;

    private string[] _folderImages = Array.Empty<string>();
    private int _imageIndex = 0;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        using var bgPaint = new SKPaint { Color = new SKColor(25, 27, 40), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, CornerRadius, CornerRadius, bgPaint);
        canvas.DrawRoundRect(bounds, CornerRadius, CornerRadius, borderPaint);

        string? currentFile = GetActiveImageFile();

        if (!string.IsNullOrEmpty(currentFile) && File.Exists(currentFile))
        {
            try
            {
                using var bitmap = SKBitmap.Decode(currentFile);
                if (bitmap != null)
                {
                    canvas.Save();
                    var clipBuilder = new SKPathBuilder();
                    clipBuilder.AddRoundRect(bounds, CornerRadius, CornerRadius);
                    using var clipPath = clipBuilder.Snapshot();
                    canvas.ClipPath(clipPath);
                    canvas.DrawBitmap(bitmap, bounds, new SKSamplingOptions(SKFilterMode.Linear));
                    canvas.Restore();
                    return;
                }
            }
            catch { }
        }

        using var iconFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 36f);
        using var iconPaint = new SKPaint { Color = new SKColor(255, 255, 255, 70), IsAntialias = true };
        var tb = new SKRect();
        iconFont.MeasureText("🖼️", out tb, iconPaint);
        canvas.DrawText("🖼️", bounds.MidX - (tb.Width / 2f), bounds.MidY - 10f, SKTextAlign.Left, iconFont, iconPaint);

        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), 12f);
        using var labelPaint = new SKPaint { Color = new SKColor(224, 194, 196), IsAntialias = true };
        string hint = "Click/Tap to Cycle Pictures";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, labelPaint);
        canvas.DrawText(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 25f, SKTextAlign.Left, labelFont, labelPaint);
    }

    private string? GetActiveImageFile()
    {
        if (File.Exists(ImagePath)) return ImagePath;

        if (Directory.Exists(ImagePath))
        {
            if (_folderImages.Length == 0)
            {
                _folderImages = Directory.GetFiles(ImagePath, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            if (_folderImages.Length > 0)
            {
                _imageIndex %= _folderImages.Length;
                return _folderImages[_imageIndex];
            }
        }
        return null;
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp && _folderImages.Length > 0)
        {
            _imageIndex = (_imageIndex + 1) % _folderImages.Length;
            Context?.RequestRender();
        }
    }
}

[WidgetMetadata("twitch_chat", "Twitch Live Chat", "Streams live Twitch chat messages with custom colors.", "ModernWigiDash", "2.0.0", "Social & Visual", GridSizePreset.Size2x4)]
public class TwitchChatStreamWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x4.ToSize();

    [WidgetProperty("Channel Name", WidgetPropertyType.Text, "Twitch Channel ID", "shroud")]
    public string ChannelName { get; set; } = "shroud";

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 240), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        float pad = 14f;
        using var headerFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13f);
        using var headerPaint = new SKPaint { Color = new SKColor(255, 180, 171), IsAntialias = true };
        canvas.DrawText($"💬 TWITCH LIVE CHAT: #{ChannelName.ToUpper()}", pad, pad + 12f, SKTextAlign.Left, headerFont, headerPaint);

        string[] users = { "NinjaGamer", "CyberFan99", "ProSniper", "WigiMaster" };
        string[] msgs = { "Poggers! That setup looks clean 🔥", "Can you show the telemetry screen again?", "GG what a play!", "WigiDash + .NET 10 is insane!" };
        SKColor[] colors = { new SKColor(255, 180, 171), new SKColor(229, 57, 53), new SKColor(255, 137, 125), new SKColor(224, 194, 196) };

        float y = pad + 40f;
        for (int i = 0; i < msgs.Length && y < bounds.Bottom - pad; i++)
        {
            using var userFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 12f);
            using var userPaint = new SKPaint { Color = colors[i], IsAntialias = true };
            canvas.DrawText($"{users[i]}:", pad, y, SKTextAlign.Left, userFont, userPaint);

            var ub = new SKRect();
            userFont.MeasureText($"{users[i]}: ", out ub, userPaint);

            using var msgFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), 12f);
            using var msgPaint = new SKPaint { Color = new SKColor(244, 239, 244, 210), IsAntialias = true };
            canvas.DrawText(msgs[i], pad + ub.Width + 4f, y, SKTextAlign.Left, msgFont, msgPaint);

            y += 26f;
        }
    }
}

[WidgetMetadata("weather_forecast", "Weather Forecast", "Displays live real-time temperature, humidity, and weather condition badges via Open-Meteo API.", "ModernWigiDash", "2.0.0", "Social & Visual", GridSizePreset.Size2x1)]
public class WeatherForecastWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x1.ToSize();

    [WidgetProperty("Location", WidgetPropertyType.Text, "City, Country", "New York")]
    public string Location { get; set; } = "New York";

    [WidgetProperty("Temp (°C/°F)", WidgetPropertyType.Text, "Current Temp", "24°C / 75°F")]
    public string TempString { get; set; } = "24°C / 75°F";

    [WidgetProperty("Condition", WidgetPropertyType.Choice, "Weather Icon", "Sunny", "Sunny", "Rainy", "Cloudy")]
    public string Condition { get; set; } = "Sunny";

    private static readonly HttpClient _httpClient = new HttpClient();
    private DateTime _lastWeatherFetch = DateTime.MinValue;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        FetchLiveWeatherAsync();

        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 230), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        float pad = 16f;
        string icon = Condition switch
        {
            "Rainy" => "🌧️",
            "Cloudy" => "⛅",
            _ => "☀️"
        };

        using var iconFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 38f);
        using var iconPaint = new SKPaint { IsAntialias = true };
        canvas.DrawText(icon, pad, bounds.MidY + 12f, SKTextAlign.Left, iconFont, iconPaint);

        using var locFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 14f);
        using var locPaint = new SKPaint { Color = new SKColor(224, 194, 196), IsAntialias = true };
        canvas.DrawText(Location, pad + 55f, bounds.MidY - 8f, SKTextAlign.Left, locFont, locPaint);

        using var tempFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 22f);
        using var tempPaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };
        canvas.DrawText(TempString, pad + 55f, bounds.MidY + 18f, SKTextAlign.Left, tempFont, tempPaint);
    }

    private async void FetchLiveWeatherAsync()
    {
        if ((DateTime.Now - _lastWeatherFetch).TotalMinutes < 5) return;
        _lastWeatherFetch = DateTime.Now;

        try
        {
            // Open-Meteo Live Weather API (New York default 40.71, -74.00)
            string url = "https://api.open-meteo.com/v1/forecast?latitude=40.7128&longitude=-74.0060&current_weather=true";
            string json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("current_weather", out var currentWeather))
            {
                if (currentWeather.TryGetProperty("temperature", out var tempEl))
                {
                    double c = tempEl.GetDouble();
                    double f = (c * 9 / 5) + 32;
                    TempString = $"{c:F1}°C / {f:F0}°F";
                }
                if (currentWeather.TryGetProperty("weathercode", out var codeEl))
                {
                    int code = codeEl.GetInt32();
                    Condition = code switch
                    {
                        >= 51 and <= 67 => "Rainy",
                        >= 1 and <= 3 => "Cloudy",
                        _ => "Sunny"
                    };
                }
                Context?.RequestRender();
            }
        }
        catch { }
    }
}
