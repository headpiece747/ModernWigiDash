using System.Text.Json.Serialization;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Core.Models;

public class PlacedWidgetInstance
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public string PluginId { get; set; } = string.Empty;
    public string DisplayName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Widget" : value.Trim(); } = "Widget";

    // Absolute pixel positioning on active framebuffer (1016x592)
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set => field = Math.Max(10f, value); } = 408f;
    public float Height { get; set => field = Math.Max(10f, value); } = 300f;

    // Layering & Transparency
    public int ZIndex { get; set; }
    public float Opacity { get; set => field = Math.Clamp(value, 0f, 1f); } = 1.0f;
    public float Rotation { get; set; } = 0.0f;

    // Custom properties dictionary configured by user in the right panel
    public Dictionary<string, object?> PropertyValues { get; set; } = [];

    // Runtime active widget instance (not serialized)
    [JsonIgnore]
    public IModernWidget? ActiveInstance { get; set; }

    /// <summary>
    /// Maps a global (framebuffer) point into this widget's unrotated local
    /// space — the exact inverse of the render transform in
    /// SkiaFrameCompositor.Compose (translate to (X, Y), then rotate about the
    /// center). The two consumers of the transform (rendering and touch
    /// routing) are therefore one geometry, not two.
    /// </summary>
    public SKPoint ToLocalPoint(float pointX, float pointY)
    {
        float localX = pointX - X;
        float localY = pointY - Y;

        if (Math.Abs(Rotation) <= 0.01f)
            return new SKPoint(localX, localY);

        float radians = -Rotation * (float)(Math.PI / 180.0);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        float cx = Width / 2f;
        float cy = Height / 2f;
        float dx = localX - cx;
        float dy = localY - cy;
        return new SKPoint(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    public bool ContainsPoint(float pointX, float pointY)
    {
        SKPoint local = ToLocalPoint(pointX, pointY);
        return local.X >= 0 && local.X <= Width &&
               local.Y >= 0 && local.Y <= Height;
    }
}
