using System.Text.Json.Serialization;

namespace ModernWigiDash.Core.Models;

/// <summary>
/// A widget bound to a page: its identity, pixel placement, layering,
/// custom properties, and the live runtime instance.
/// </summary>
public class PlacedWidgetInstance
{
    /// <summary>Stable identity of this placement, assigned at creation.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>The catalog plugin id of the widget placed here.</summary>
    public string PluginId { get; set; } = string.Empty;
    /// <summary>The placement's display name; a blank assignment repairs to "Widget".</summary>
    public string DisplayName { get; set => field = string.IsNullOrWhiteSpace(value) ? "Widget" : value.Trim(); } = "Widget";

    // Absolute pixel positioning on active framebuffer (1016x592).
    // The 2×2 house default is the model-level fallback when a placement
    // arrives without a size; rehydration upgrades the omitted size to the
    // widget's declared preset (the presence flags tell the two apart — the
    // export always writes explicit sizes).
    /// <summary>Left edge in active-framebuffer pixels (1016x592).</summary>
    public float X { get; set; }
    /// <summary>Top edge in active-framebuffer pixels (1016x592).</summary>
    public float Y { get; set; }
    /// <summary>Width in pixels; a write is floored at 10 and stamps <see cref="WidthPresent"/>.</summary>
    public float Width { get; set { field = Math.Max(10f, value); WidthPresent = true; } } = GridSizePreset.Size2x2.ToSize().Width;
    /// <summary>Height in pixels; a write is floored at 10 and stamps <see cref="HeightPresent"/>.</summary>
    public float Height { get; set { field = Math.Max(10f, value); HeightPresent = true; } } = GridSizePreset.Size2x2.ToSize().Height;

    // Serialization-presence markers (never serialized): set once the
    // Width/Height setter has run, so rehydration can tell "the imported JSON
    // carried no width" (the model default still stands) from "the JSON set it
    // explicitly" (which wins).
    internal bool WidthPresent { get; private set; }
    internal bool HeightPresent { get; private set; }

    // Layering & Transparency
    /// <summary>Layering order within the page; rendered low to high.</summary>
    public int ZIndex { get; set; }
    /// <summary>Opacity in [0, 1], clamped on write; below 1 draws through the alpha layer.</summary>
    public float Opacity { get => field; set => field = Math.Clamp(value, 0f, 1f); } = 1.0f;
    // Normalized to [0, 360) on write so a profile with 720° (or negative
    // degrees) renders and hit-tests identically to the equivalent rotation.
    /// <summary>Rotation in degrees, normalized to [0, 360) on write.</summary>
    public float Rotation { get => field; set => field = ((value % 360f) + 360f) % 360f; } = 0.0f;

    // Custom properties dictionary configured by user in the right panel
    /// <summary>Custom property values configured in the inspector, persisted with the profile.</summary>
    public Dictionary<string, object?> PropertyValues { get; set; } = [];

    // Runtime active widget instance (not serialized)
    /// <summary>The live widget instance backing this placement (runtime only, never serialized).</summary>
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

    /// <summary>
    /// Whether a global (framebuffer) point falls inside this placement's
    /// unrotated rectangle, tested in its rotated-local space (via
    /// <see cref="ToLocalPoint"/>).
    /// </summary>
    /// <param name="pointX">Global X in framebuffer pixels.</param>
    /// <param name="pointY">Global Y in framebuffer pixels.</param>
    /// <returns>True when the point is inside the placement's footprint.</returns>
    public bool ContainsPoint(float pointX, float pointY)
    {
        SKPoint local = ToLocalPoint(pointX, pointY);
        return local.X >= 0 && local.X <= Width &&
               local.Y >= 0 && local.Y <= Height;
    }
}
