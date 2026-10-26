using System.Reflection;
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A tappable button that runs one hotkey action (the HotkeyActionCatalog
/// kind and its path/command) on release, with a label and description line,
/// the button colors, and an optional Griddy or custom-SVG icon that edit
/// mode can drag (the IWidgetIconGrab seam).
/// Rendering is delegated to <see cref="HotkeyButtonCanvas"/> and geometry to <see cref="HotkeyIconGeometry"/>.
/// </summary>
[WidgetMetadata("hotkey_button", "Hotkey", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class HotkeyButtonWidget : ModernWidgetBase, IWidgetEditorProvider, IWidgetIconGrab, IGlobalHotkeyProvider
{
    /// <summary>The "Button Label": the text displayed on the button.</summary>
    [WidgetProperty("Button Label", WidgetPropertyType.Text, "Text displayed on button", "Hotkey")]
    public string ButtonLabel { get; set; } = "Hotkey";

    /// <summary>The "Description": optional secondary text shown below the button label.</summary>
    [WidgetProperty("Description", WidgetPropertyType.Text, "Optional secondary text displayed below the button label", "Tap to run")]
    public string Description { get; set; } = "Tap to run";

    /// <summary>The "Action Type": which trigger the tap runs (the HotkeyActionCatalog name set).</summary>
    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", HotkeyActionCatalog.DefaultName, "Launch App", "Open URL", "Media Play / Pause", "Media Next", "Media Previous", "Media Stop", "Volume Up", "Volume Down", "Mute", "Next Page", "Previous Page", "Run AHK Script")]
    public string ActionType { get; set; } = HotkeyActionCatalog.DefaultName;

    /// <summary>The "Global Hotkey": the OS-level chord that fires this button (blank = none; edited with the key-capture editor).</summary>
    [WidgetProperty("Global Hotkey", WidgetPropertyType.Text, "OS-level hotkey that fires this button (blank = none).", "")]
    public string GlobalHotkey { get; set; } = "";

    /// <summary>The "Action Path/Command": the executable, file, folder, or URL the action type targets.</summary>
    [WidgetProperty("Action Path/Command", WidgetPropertyType.Path, "Executable, file, folder, or URL. You can type a URL or select a local path.", "")]
    public string ActionCommand { get; set; } = "";

    /// <summary>The "Button Color Hex": the button's glow accent color.</summary>
    [WidgetProperty("Button Color Hex", WidgetPropertyType.Color, "Button glow accent color", "#F59E0B")]
    public string ButtonColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color": the button label color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Button label color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Icon": the Griddy icon shown above the label (blank = none).</summary>
    [WidgetProperty("Icon", WidgetPropertyType.Icon, "Griddy icon shown above the label (blank = none)", "")]
    public string Icon { get; set; } = "";

    /// <summary>The "Icon File": a custom SVG icon file copied into the icons folder; overrides Icon.</summary>
    [WidgetProperty("Icon File", WidgetPropertyType.Path, "Custom SVG icon file copied into the icons folder (overrides Icon)", "")]
    public string IconFile { get; set; } = "";

    /// <summary>The "Icon Color": the icon color.</summary>
    [WidgetProperty("Icon Color", WidgetPropertyType.Color, "Icon color", "#FAFAFA")]
    public string IconColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Icon Size" in px (0 = auto-scale with the widget).</summary>
    [WidgetProperty("Icon Size", WidgetPropertyType.Number, "Icon size in px (0 = auto-scale with the widget)", 0)]
    public int IconSize { get; set; } = 0;

    /// <summary>The "Icon Offset X": horizontal shift of the icon in px (negative = left).</summary>
    [WidgetProperty("Icon Offset X", WidgetPropertyType.Number, "Horizontal shift of the icon in px (negative = left)", 0)]
    public int IconOffsetX { get; set; } = 0;

    /// <summary>The "Icon Offset Y": vertical shift of the icon in px (negative = up).</summary>
    [WidgetProperty("Icon Offset Y", WidgetPropertyType.Number, "Vertical shift of the icon in px (negative = up)", 0)]
    public int IconOffsetY { get; set; } = 0;

    private readonly HotkeyButtonCanvas _canvas = new();

    private bool HasIcon => !string.IsNullOrWhiteSpace(IconFile)
        ? SvgIconLoader.TryGetPath(IconFile, out _)
        : HasGriddyIcon;

    /// <summary>
    /// Whether the rotated-local point falls inside the drawn icon's hit
    /// circle (the edit-mode icon-grab region); false when no icon is drawn.
    /// </summary>
    public bool IsPointOverIcon(float width, float height, float localX, float localY)
        => HotkeyIconGeometry.IsPointOver(width, height, IconSize, IconOffsetX, IconOffsetY, HasIcon, localX, localY);

    /// <summary>
    /// The icon's center and radius for the given bounds, so the input
    /// module can grab and move the icon; false when no icon is drawn.
    /// </summary>
    public bool TryGetIconCenter(float width, float height, out SKPoint center, out float half)
        => HotkeyIconGeometry.Compute(width, height, IconSize, IconOffsetX, IconOffsetY, HasIcon, out center, out half);

    /// <summary>
    /// Persists the icon's new offset after an edit-mode grab-move (the
    /// center is clamped inside the widget bounds); commits through
    /// SetProperty so the move survives export.
    /// </summary>
    public bool ApplyGrabMove(PlacedWidgetInstance placed, float localX, float localY, float grabOffsetX, float grabOffsetY)
    {
        if (!HotkeyIconGeometry.TryCalculateGrabMove(placed.Width, placed.Height, IconSize, IconOffsetX, IconOffsetY, HasIcon, localX, localY, grabOffsetX, grabOffsetY, out int newX, out int newY))
            return false;

        SetProperty(nameof(IconOffsetX), newX);
        SetProperty(nameof(IconOffsetY), newY);
        return true;
    }

    // The Griddy-icon probe is memoized per icon name: the icon set is static,
    // so a probed name's result cannot change — Render hit-tests the icon every
    // frame, and Contains would otherwise Trim-allocate on every call.
    private string _lastGriddyIcon = "";
    private bool _lastGriddyIconResult;

    private bool HasGriddyIcon
    {
        get
        {
            if (!string.Equals(Icon, _lastGriddyIcon, StringComparison.Ordinal))
            {
                _lastGriddyIcon = Icon;
                _lastGriddyIconResult = !string.IsNullOrWhiteSpace(Icon) && GriddyIcons.Contains(Icon);
            }
            return _lastGriddyIconResult;
        }
    }

    private bool _isPressed = false;
    // The action-fire ritual (gate + CTS + dispatch) in one module, driven
    // through the providers: the touch-up and the global-hotkey trigger both
    // call RunAsync; teardown runs DisposeAsync.
    private readonly HotkeyActionRunner _runner;
    // The missing/unsupported icon-file error is reported once per path change
    // — Render runs at 30 FPS and a bad path must not spam the log every frame.
    private string? _lastIconErrorPath;

    /// <summary>
    /// The reflection-instantiated widget entry (the parameterless ctor the
    /// App's plugin loader requires): wires the action-fire ritual to the
    /// runner through the four provider seams.
    /// </summary>
    public HotkeyButtonWidget()
    {
        _runner = new(
            () => HotkeyActionCatalog.Create(ActionType, ActionCommand),
            () => ActionType,
            () => Context,
            () => ActionExecutor);
    }

    /// <summary>
    /// Test seam for action execution. Defaults to
    /// <see cref="HotkeyActionExecutor.ExecuteAsync"/>; tests inject a fake so
    /// the press path (gate, skip, failure logging) can be exercised without
    /// launching processes or sending keys.
    /// </summary>
    internal Func<IReadOnlyList<HotkeyAction>, CancellationToken, Task>? ActionExecutor { get; set; }

    /// <summary>
    /// Draws the button: the pressed-state glow, the label and description
    /// text, and the icon (custom SVG file or Griddy) in front of them.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor btnColor = ColorOf(ButtonColorHex, WidgetPalette.Accent);
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor iconColor = ColorOf(IconColorHex, SKColors.White);

        bool useCustomFile = !string.IsNullOrWhiteSpace(IconFile);
        bool hasGeometry = HotkeyIconGeometry.Compute(bounds.Width, bounds.Height, IconSize, IconOffsetX, IconOffsetY, HasIcon, out var iconCenter, out float half);

        SKPath? resolvedPath = null;
        if (hasGeometry && useCustomFile)
        {
            SvgIconLoader.TryGetPath(IconFile, out resolvedPath);
        }
        else if (!hasGeometry && useCustomFile && !string.Equals(_lastIconErrorPath, IconFile, StringComparison.Ordinal))
        {
            _lastIconErrorPath = IconFile;
            Context?.LogError($"Hotkey custom icon file not found or unsupported: {IconFile}");
        }

        if (hasGeometry && useCustomFile) _lastIconErrorPath = null;

        _canvas.Render(
            canvas,
            bounds,
            _isPressed,
            ButtonLabel,
            Description,
            hasGeometry,
            iconCenter,
            half,
            useCustomFile,
            resolvedPath,
            Icon,
            btnColor,
            textColor,
            iconColor);
    }

    /// <summary>
    /// Tracks the pressed state on TouchDown and runs the configured action
    /// fire-and-forget on TouchUp.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _isPressed = true;
            Context?.RequestRender();
        }
        else if (eventType == TouchEventType.TouchUp)
        {
            _isPressed = false;
            _ = _runner.RunAsync();
            Context?.RequestRender();
        }
    }

    /// <summary>
    /// The special inspector editor for this widget's properties: the icon
    /// picker for IconFile, the action-command editor for ActionCommand, or
    /// null when the generic editor suffices.
    /// </summary>
    public EditorKind? GetEditorKind(PropertyInfo property)
    {
        if (string.Equals(property.Name, nameof(IconFile), StringComparison.Ordinal)) return EditorKind.IconPicker;
        if (string.Equals(property.Name, nameof(ActionCommand), StringComparison.Ordinal)) return EditorKind.ActionCommand;
        if (string.Equals(property.Name, nameof(GlobalHotkey), StringComparison.Ordinal)) return EditorKind.KeyCapture;
        return null;
    }

    /// <summary>
    /// The companion property written alongside the named-icon editor: the
    /// IconFile path property that overrides Icon, or null for other
    /// properties.
    /// </summary>
    public PropertyInfo? GetIconFileCompanion(PropertyInfo iconProperty)
        => string.Equals(iconProperty.Name, nameof(Icon), StringComparison.Ordinal)
            ? typeof(HotkeyButtonWidget).GetProperty(nameof(IconFile))
            : null;

    /// <summary>The choice property (Action Type) whose selected value toggles the action-command editor's visibility.</summary>
    public string? ActionCommandVisibilityChoicePropertyName => nameof(ActionType);

    /// <summary>
    /// Whether the action-command editor is visible for the selected action
    /// type: only types that need a command (the HotkeyActionCatalog rule).
    /// </summary>
    public bool IsActionCommandVisible(string? actionTypeValue)
        => actionTypeValue != null && HotkeyActionCatalog.NeedsCommand(actionTypeValue);

    // -- IGlobalHotkeyProvider (the window's discovery seam) -------------------
    bool IGlobalHotkeyProvider.TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord)
    {
        if (GlobalHotkeyChordPolicy.TryParseChord(GlobalHotkey, out modFlags, out virtualKey))
        {
            chord = GlobalHotkey.Trim();
            return true;
        }
        modFlags = 0;
        virtualKey = 0;
        chord = "";
        return false;
    }

    void IGlobalHotkeyProvider.FireGlobalHotkey() => _ = _runner.RunAsync();

    bool IGlobalHotkeyProvider.AffectsGlobalHotkey(string propertyName)
        => string.Equals(propertyName, nameof(GlobalHotkey), StringComparison.Ordinal);

    /// <summary>Disposes the hoisted paints and the action runner (which cancels any in-flight action and releases its gate).</summary>
    public override async ValueTask DisposeAsync()
    {
        _canvas.Dispose();
        await _runner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
