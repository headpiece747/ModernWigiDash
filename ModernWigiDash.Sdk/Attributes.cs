namespace ModernWigiDash.Sdk;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class WidgetMetadataAttribute : Attribute
{
    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; set; } = "";
    public string Author { get; set; } = "Community";
    public string Version { get; set; } = "1.0.0";
    public string Category { get; set; } = "General";
    public GridSizePreset DefaultGridSize { get; set; } = GridSizePreset.Size2x2;

    /// <summary>
    /// Only the required identity fields are positional; optional metadata is
    /// set via named properties so adding a field never breaks existing usages.
    /// </summary>
    public WidgetMetadataAttribute(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }
}

public enum WidgetPropertyType
{
    Text,
    Number,
    Boolean,
    Color,
    Choice,
    Font,
    Icon,
    SensorSelector,
    Path,
    ActionList,
    Button
}

public sealed record WidgetPropertyOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public interface IWidgetPropertyOptionsProvider
{
    IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName);
}

public interface IWidgetActionPresentationProvider
{
    string? GetWidgetActionLabel(string propertyName);
    bool IsWidgetActionActive(string propertyName);
}

[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class WidgetPropertyAttribute : Attribute
{
    public string DisplayName { get; }
    public string Description { get; }
    public WidgetPropertyType PropertyType { get; }
    public object? DefaultValue { get; }
    public string[] Options { get; }

    public WidgetPropertyAttribute(
        string displayName,
        WidgetPropertyType propertyType = WidgetPropertyType.Text,
        string description = "",
        object? defaultValue = null,
        params string[] options)
    {
        DisplayName = displayName;
        PropertyType = propertyType;
        Description = description;
        DefaultValue = defaultValue;
        Options = options ?? [];
    }
}
