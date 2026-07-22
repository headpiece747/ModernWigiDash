namespace ModernWigiDash.Sdk;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class WidgetMetadataAttribute : Attribute
{
    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string Author { get; }
    public string Version { get; }
    public string Category { get; }
    public GridSizePreset DefaultGridSize { get; }

    public WidgetMetadataAttribute(
        string id,
        string displayName,
        string description = "",
        string author = "Community",
        string version = "1.0.0",
        string category = "General",
        GridSizePreset defaultGridSize = GridSizePreset.Size2x2)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Author = author;
        Version = version;
        Category = category;
        DefaultGridSize = defaultGridSize;
    }
}

public enum WidgetPropertyType
{
    Text,
    Number,
    Boolean,
    Color,
    Choice,
    SensorSelector
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
        Options = options ?? Array.Empty<string>();
    }
}
