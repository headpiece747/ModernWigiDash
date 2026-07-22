using System.Text.Json.Serialization;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Models;

public class PlacedWidgetInstance
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public string PluginId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Widget";

    // Absolute pixel positioning on 1024x600 canvas
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 408f;
    public float Height { get; set; } = 300f;

    // Layering & Transparency
    public int ZIndex { get; set; }
    public float Opacity { get; set; } = 1.0f;
    public float Rotation { get; set; } = 0.0f;

    // Custom properties dictionary configured by user in the right panel
    public Dictionary<string, object?> PropertyValues { get; set; } = new();

    // Runtime active widget instance (not serialized)
    [JsonIgnore]
    public IModernWidget? ActiveInstance { get; set; }

    public bool ContainsPoint(float pointX, float pointY)
    {
        return pointX >= X && pointX <= X + Width &&
               pointY >= Y && pointY <= Y + Height;
    }
}
