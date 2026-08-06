using System.Text.Json.Serialization;
using ModernWigiDash.Sdk;

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

    public bool ContainsPoint(float pointX, float pointY)
    {
        return pointX >= X && pointX <= X + Width &&
               pointY >= Y && pointY <= Y + Height;
    }
}
