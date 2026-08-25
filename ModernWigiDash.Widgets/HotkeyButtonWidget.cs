using System.Reflection;
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A tappable button that runs one hotkey action (the HotkeyActionCatalog
/// kind and its path/command) on release, with a label and description line,
/// the button colors, and an optional Griddy or custom-SVG icon that edit
/// mode can drag (the IWidgetIconGrab seam).
/// </summary>
[WidgetMetadata("hotkey_button", "Hotkey", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class HotkeyButtonWidget : ModernWidgetBase, IWidgetEditorProvider, IWidgetIconGrab
{
    /// <summary>The "Button Label": the text displayed on the button.</summary>
    [WidgetProperty("Button Label", WidgetPropertyType.Text, "Text displayed on button", "Hotkey")]
    public string ButtonLabel { get; set; } = "Hotkey";

    /// <summary>The "Description": optional secondary text shown below the button label.</summary>
    [WidgetProperty("Description", WidgetPropertyType.Text, "Optional secondary text displayed below the button label", "Tap to run")]
    public string Description { get; set; } = "Tap to run";

    /// <summary>The "Action Type": which trigger the tap runs (the HotkeyActionCatalog name set).</summary>
    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", HotkeyActionCatalog.DefaultName, "Launch App", "Open URL", "Media Play / Pause", "Media Next", "Media Previous", "Media Stop", "Volume Up", "Volume Down", "Mute")]
    public string ActionType { get; set; } = HotkeyActionCatalog.DefaultName;

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

    // The icon geometry (0.62f max ratio, 0.4f auto-size, 0.31f anchor)
    // lives exactly here, in the widget that draws the icon — Render, hit
    // testing, and grab-move math all derive from one helper.

    /// <summary>
    /// Whether the rotated-local point falls inside the drawn icon's hit
    /// circle (the edit-mode icon-grab region); false when no icon is drawn.
    /// </summary>
    /// <param name="width">The widget's width in px.</param>
    /// <param name="height">The widget's height in px.</param>
    /// <param name="localX">The point's x in the widget's rotated-local coordinates.</param>
    /// <param name="localY">The point's y in the widget's rotated-local coordinates.</param>
    /// <returns>True when the point is inside the icon's hit circle.</returns>
    public bool IsPointOverIcon(float width, float height, float localX, float localY)
    {
        if (!ComputeIconGeometry(width, height, out var center, out float half))
            return false;

        float dx = localX - center.X;
        float dy = localY - center.Y;
        return dx * dx + dy * dy <= half * half;
    }

    /// <summary>
    /// The icon's center and radius for the given bounds, so the input
    /// module can grab and move the icon; false when no icon is drawn.
    /// </summary>
    /// <param name="width">The widget's width in px.</param>
    /// <param name="height">The widget's height in px.</param>
    /// <param name="center">The icon's center in widget coordinates.</param>
    /// <param name="half">The icon's radius in px.</param>
    /// <returns>True when an icon is drawn.</returns>
    public bool TryGetIconCenter(float width, float height, out SKPoint center, out float half)
        => ComputeIconGeometry(width, height, out center, out half);

    /// <summary>
    /// Persists the icon's new offset after an edit-mode grab-move (the
    /// center is clamped inside the widget bounds); commits through
    /// SetProperty so the move survives export.
    /// </summary>
    /// <param name="placed">The placed instance being moved (its bounds).</param>
    /// <param name="localX">The icon's center x in the widget's rotated-local coordinates.</param>
    /// <param name="localY">The icon's center y in the widget's rotated-local coordinates.</param>
    /// <param name="grabOffsetX">The x distance between the grab point and the icon center.</param>
    /// <param name="grabOffsetY">The y distance between the grab point and the icon center.</param>
    /// <returns>True when the offset changed and was persisted.</returns>
    public bool ApplyGrabMove(PlacedWidgetInstance placed, float localX, float localY, float grabOffsetX, float grabOffsetY)
    {
        if (!ComputeIconGeometry(placed.Width, placed.Height, out _, out float half))
            return false;

        float cx = Math.Clamp(localX + grabOffsetX, half, placed.Width - half);
        float cy = Math.Clamp(localY + grabOffsetY, half, placed.Height - half);
        int newX = (int)Math.Round(cx - placed.Width / 2f);
        int newY = (int)Math.Round(cy - placed.Height * 0.31f);
        if (newX == IconOffsetX && newY == IconOffsetY)
            return false;

        // SetProperty covers instance + OnPropertyChanged + PropertyValues
        // persistence — one write path for properties that must survive export.
        SetProperty(nameof(IconOffsetX), newX);
        SetProperty(nameof(IconOffsetY), newY);
        return true;
    }

    /// <summary>Icon center and half-size for the given bounds; false when no icon is drawn.</summary>
    private bool ComputeIconGeometry(float width, float height, out SKPoint center, out float half)
    {
        bool useCustomFile = !string.IsNullOrWhiteSpace(IconFile);
        bool hasIcon = useCustomFile
            ? SvgIconLoader.TryGetPath(IconFile, out _)
            : HasGriddyIcon;
        if (!hasIcon)
        {
            center = default;
            half = 0f;
            return false;
        }

        float maxIconSize = Math.Min(width, height * 0.62f);
        float iconSize = IconSize > 0 ? IconSize : Math.Min(width, height) * 0.4f;
        iconSize = Math.Clamp(iconSize, 0f, maxIconSize);
        half = iconSize / 2f;
        if (half <= 0f)
        {
            center = default;
            return false;
        }

        center = new SKPoint(
            Math.Clamp(width / 2f + IconOffsetX, half, width - half),
            Math.Clamp(height * 0.31f + IconOffsetY, half, height - half));
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
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _actionCts;
    // The missing/unsupported icon-file error is reported once per path change
    // — Render runs at 30 FPS and a bad path must not spam the log every frame.
    private string? _lastIconErrorPath;

    /// <summary>
    /// Test seam for action execution. Defaults to
    /// <see cref="HotkeyActionExecutor.ExecuteAsync"/>; tests inject a fake so
    /// the press path (gate, skip, failure logging) can be exercised without
    /// launching processes or sending keys.
    /// </summary>
    internal Func<IReadOnlyList<HotkeyAction>, CancellationToken, Task>? ActionExecutor { get; set; }

    // Hoisted paints: the colors mutate per render (property-driven), so the
    // 30 FPS render allocates no SKPaint. DrawLabelOnly shares the text pair.
    private readonly SKPaint _fillPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _descriptionPaint = new() { IsAntialias = true };

    /// <summary>
    /// Draws the button: the pressed-state glow, the label and description
    /// text, and the icon (custom SVG file or Griddy) in front of them.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor btnColor = ColorOf(ButtonColorHex, WidgetPalette.Accent);
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor iconColor = ColorOf(IconColorHex, SKColors.White);

        if (_isPressed)
        {
            _fillPaint.Color = btnColor.WithAlpha(180);
            canvas.DrawRoundRect(bounds, 16f, 16f, _fillPaint);
        }

        string label = ButtonLabel;

        bool useCustomFile = !string.IsNullOrWhiteSpace(IconFile);
        if (!ComputeIconGeometry(bounds.Width, bounds.Height, out var iconCenter, out float half))
        {
            if (useCustomFile && !string.Equals(_lastIconErrorPath, IconFile, StringComparison.Ordinal))
            {
                _lastIconErrorPath = IconFile;
                Context?.LogError($"Hotkey custom icon file not found or unsupported: {IconFile}");
            }
            DrawLabelOnly(canvas, bounds, label, textColor, Description);
            return;
        }
        if (useCustomFile) _lastIconErrorPath = null;
        float iconSize = half * 2f;
        SKPath? resolvedPath = null;
        if (useCustomFile)
            SvgIconLoader.TryGetPath(IconFile, out resolvedPath);

        // Draw label and description first so the icon can render in front of them
        float labelSize = Math.Min(bounds.Width / 7f, bounds.Height / 7f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, labelSize);
        _textPaint.Color = textColor;
        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, _textPaint);
        canvas.DrawTextWithFallback(label, bounds.MidX - textBounds.Width / 2f,
            bounds.Top + bounds.Height * 0.78f, font, _textPaint);

        if (!string.IsNullOrWhiteSpace(Description))
        {
            var descriptionFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, labelSize * 0.6f));
            _descriptionPaint.Color = textColor.WithAlpha(180);
            descriptionFont.MeasureText(Description, out var descriptionBounds, _descriptionPaint);
            canvas.DrawTextWithFallback(Description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(8f, labelSize * 0.4f), descriptionFont, _descriptionPaint);
        }

        // Icon drawn last so it stays in front of the text when overlapped
        if (useCustomFile)
            SvgIconLoader.Draw(canvas, resolvedPath!, iconCenter, iconSize, iconColor, 0, 0);
        else
            GriddyIcons.Draw(canvas, Icon, iconCenter, iconSize, iconColor, 0, 0);
    }

    private void DrawLabelOnly(SKCanvas canvas, SKRect bounds, string label, SKColor textColor, string description)
    {
        float fontSize = Math.Min(bounds.Width / 6f, bounds.Height / 5f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize);
        _textPaint.Color = textColor;

        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, _textPaint);
        canvas.DrawTextWithFallback(label, bounds.MidX - textBounds.Width / 2f, bounds.MidY - textBounds.Height / 4f, font, _textPaint);

        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, fontSize * 0.42f));
            _descriptionPaint.Color = textColor.WithAlpha(180);
            descriptionFont.MeasureText(description, out var descriptionBounds, _descriptionPaint);
            canvas.DrawTextWithFallback(description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(12f, fontSize * 0.65f), descriptionFont, _descriptionPaint);
        }
    }

    /// <summary>
    /// Tracks the pressed state on TouchDown and runs the configured action
    /// fire-and-forget on TouchUp.
    /// </summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local coordinates.</param>
    /// <param name="eventType">The touch event type.</param>
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
            _ = ExecuteActionsAsync();
            Context?.RequestRender();
        }
    }

    private async Task ExecuteActionsAsync()
    {
        // Zero-timeout try-acquire: returns immediately, so there is no wait to
        // cancel; the per-run _actionCts is created after the gate is taken.
        if (!await _actionGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return;
        if (_actionCts is { } prior)
        {
            await prior.CancelAsync().ConfigureAwait(false);
            prior.Dispose();
        }
        _actionCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var action = HotkeyActionCatalog.Create(ActionType, ActionCommand);
            if (string.IsNullOrWhiteSpace(action.Value) && HotkeyActionCatalog.NeedsCommand(ActionType))
            {
                Context?.LogError("Hotkey action skipped: Action Path/Command is empty.");
                return;
            }
            var executor = ActionExecutor ?? HotkeyActionExecutor.ExecuteAsync;
            await executor([action], _actionCts.Token).ConfigureAwait(false);
            Context?.RequestRender();
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Hotkey action cancelled (30s timeout or shutdown)");
        }
        catch (Exception ex)
        {
            Context?.LogError($"Hotkey action failed: {ex.Message}", ex);
        }
        finally
        {
            _actionCts.Dispose();
            _actionCts = null;
            _actionGate.Release();
        }
    }

    // The inspector renderer discovers these through the interface instead of
    // branching on the widget type (no concrete-widget typeof checks).

    /// <summary>
    /// The special inspector editor for this widget's properties: the icon
    /// picker for IconFile, the action-command editor for ActionCommand, or
    /// null when the generic editor suffices.
    /// </summary>
    /// <param name="property">The property being inspected.</param>
    /// <returns>The editor kind, or null for the generic editor.</returns>
    public EditorKind? GetEditorKind(PropertyInfo property)
    {
        if (string.Equals(property.Name, nameof(IconFile), StringComparison.Ordinal)) return EditorKind.IconPicker;
        if (string.Equals(property.Name, nameof(ActionCommand), StringComparison.Ordinal)) return EditorKind.ActionCommand;
        return null;
    }

    /// <summary>
    /// The companion property written alongside the named-icon editor: the
    /// IconFile path property that overrides Icon, or null for other
    /// properties.
    /// </summary>
    /// <param name="iconProperty">The named-icon property being edited.</param>
    /// <returns>The companion file property, or null.</returns>
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
    /// <param name="actionTypeValue">The selected action type value.</param>
    /// <returns>True when the selected type needs a command.</returns>
    public bool IsActionCommandVisible(string? actionTypeValue)
        => actionTypeValue != null && HotkeyActionCatalog.NeedsCommand(actionTypeValue);


    /// <summary>Cancels any in-flight action and disposes the hoisted paints and the action gate.</summary>
    public override async ValueTask DisposeAsync()
    {
        _fillPaint.Dispose();
        _textPaint.Dispose();
        _descriptionPaint.Dispose();
        if (_actionCts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }
        _actionGate.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
