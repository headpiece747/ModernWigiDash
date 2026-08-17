namespace ModernWigiDash.Sdk;

/// <summary>
/// The registration metadata every widget class carries: the plugin identity
/// the catalog (<c>WidgetPluginLoader</c>) exposes, the display name and
/// category shown in the widget picker, the nominal default grid size
/// placement and the catalog use, and the persisted widget-type key used in
/// profiles. One attribute per widget class, on the class itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class WidgetMetadataAttribute : Attribute
{
    /// <summary>The unique plugin id — the widget-type key persisted in
    /// profiles and used to rehydrate placements. Must be unique across the
    /// catalog; changing it breaks existing profiles.</summary>
    public string Id { get; }

    /// <summary>The label shown in the widget picker and catalog.</summary>
    public string DisplayName { get; }

    /// <summary>The catalog grouping the widget appears under; defaults to
    /// "General".</summary>
    public string Category { get; set; } = "General";

    /// <summary>
    /// The nominal size the widget takes when placed without an explicit
    /// size (placement centering and the catalog's placement facts). The
    /// instance's <see cref="IModernWidget.DefaultSize"/> derives from this,
    /// and the catalog resolves it once at registration — so a widget that
    /// declares its preset here needs no override and placement never
    /// constructs a probe instance to learn the value. Defaults to the 2×2
    /// house size. A non-preset default can still override
    /// <see cref="IModernWidget.DefaultSize"/>.
    /// </summary>
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

/// <summary>
/// The inspector editor kinds a <c>[WidgetProperty]</c> can declare. Each maps
/// to one editor row in the settings panel; widgets opt into dynamic choices
/// (<see cref="IWidgetPropertyOptionsProvider"/>) and action buttons
/// (<see cref="IWidgetActionInvoker"/>) where noted.
/// </summary>
public enum WidgetPropertyType
{
    /// <summary>Single-line text input.</summary>
    Text,
    /// <summary>Numeric input.</summary>
    Number,
    /// <summary>Checkbox (true/false).</summary>
    Boolean,
    /// <summary>Color picker; values are stored as #RRGGBB hex strings — or
    /// #AARRGGBB when the widget's picker sets an alpha below 255.</summary>
    Color,
    /// <summary>Dropdown of the attribute's static options, or the widget's
    /// dynamic options via <see cref="IWidgetPropertyOptionsProvider"/>.</summary>
    Choice,
    /// <summary>Font-family picker.</summary>
    Font,
    /// <summary>Icon picker; the chosen icon path is written to the property.</summary>
    Icon,
    /// <summary>Dropdown of live hardware sensor readings (the shared sensor
    /// store provides the labels; the widget reads the selected reading by
    /// label at render time).</summary>
    SensorSelector,
    /// <summary>File path with a browse button.</summary>
    Path,
    /// <summary>Action button — the widget exposes the handler through
    /// <see cref="IWidgetActionInvoker"/> and may customize label/active state
    /// via <see cref="IWidgetActionPresentationProvider"/>.</summary>
    Button
}

/// <summary>One dropdown entry: the persisted value and its display label.
/// Displayed via <see cref="DisplayName"/>; the combo binds
/// SelectedValue to <see cref="Value"/>.</summary>
public sealed record WidgetPropertyOption(string Value, string DisplayName)
{
    /// <summary>Dropdowns display the label.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// Optional widget contract for dynamic choice lists: the host asks the widget
/// for the options of a Choice property when the inspector builds, and a
/// non-empty result wins over the attribute's static options. Used e.g. by the
/// Twitch chat widget for the followed-channels list.
/// </summary>
public interface IWidgetPropertyOptionsProvider
{
    /// <summary>Host call when building the inspector: the options for
    /// <paramref name="propertyName"/>, or an empty list to fall back to the
    /// attribute's static options.</summary>
    IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName);
}

/// <summary>
/// Optional widget contract for <see cref="WidgetPropertyType.Button"/> rows:
/// customize the action button's label and active state instead of showing
/// defaults. The host calls these when building/refreshing the inspector.
/// </summary>
public interface IWidgetActionPresentationProvider
{
    /// <summary>Custom label for the button of <paramref name="propertyName"/>,
    /// or null for the host default (the property's display name).</summary>
    string? GetWidgetActionLabel(string propertyName);

    /// <summary>Whether the button renders in its active state (e.g. "logged
    /// in" for a Twitch login button).</summary>
    bool IsWidgetActionActive(string propertyName);
}

/// <summary>
/// Declares an inspector-editable property on a widget: the row's display
/// name, editor kind, description, default value, and static options. Put it
/// on public instance properties with a public getter and setter — the host
/// reads the current value and writes back through the single
/// ApplyInspectorPropertyValue seam; <c>ModernWidgetBase.SetProperty</c> is
/// the widget-side write path that keeps the persisted value in sync.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class WidgetPropertyAttribute : Attribute
{
    /// <summary>The inspector row label.</summary>
    public string DisplayName { get; }

    /// <summary>Help text shown with the row.</summary>
    public string Description { get; }

    /// <summary>The editor kind; see <see cref="WidgetPropertyType"/>.</summary>
    public WidgetPropertyType PropertyType { get; }

    /// <summary>The value used when the instance property is null at inspector
    /// build time.</summary>
    public object? DefaultValue { get; }

    /// <summary>Static dropdown options (for Choice properties); the widget's
    /// <see cref="IWidgetPropertyOptionsProvider"/> may override them.</summary>
    public string[] Options { get; }

    /// <param name="displayName">The inspector row label.</param>
    /// <param name="propertyType">The editor kind (defaults to Text).</param>
    /// <param name="description">Help text shown with the row.</param>
    /// <param name="defaultValue">Used when the instance value is null.</param>
    /// <param name="options">Static Choice options.</param>
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
