using System.Reflection;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("hotkey_button", "Hotkey", Category = "Utilities")]
public class HotkeyButtonWidget : ModernWidgetBase, IWidgetEditorProvider, IWidgetIconGrab
{
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Button Label", WidgetPropertyType.Text, "Text displayed on button", "Hotkey")]
    public string ButtonLabel { get; set; } = "Hotkey";

    [WidgetProperty("Description", WidgetPropertyType.Text, "Optional secondary text displayed below the button label", "Tap to run")]
    public string Description { get; set; } = "Tap to run";

    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", "Launch App", "Launch App", "Open URL", "Media Play / Pause", "Media Next", "Media Previous", "Media Stop", "Volume Up", "Volume Down", "Mute")]
    public string ActionType { get; set; } = "Launch App";

    [WidgetProperty("Action Path/Command", WidgetPropertyType.Path, "Executable, file, folder, or URL. You can type a URL or select a local path.", "")]
    public string ActionCommand { get; set; } = "";

    [WidgetProperty("Button Color Hex", WidgetPropertyType.Color, "Button glow accent color", "#F59E0B")]
    public string ButtonColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Button label color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Icon", WidgetPropertyType.Icon, "Griddy icon shown above the label (blank = none)", "")]
    public string Icon { get; set; } = "";

    [WidgetProperty("Icon File", WidgetPropertyType.Path, "Custom SVG icon file copied into the icons folder (overrides Icon)", "")]
    public string IconFile { get; set; } = "";

    [WidgetProperty("Icon Color", WidgetPropertyType.Color, "Icon color", "#FAFAFA")]
    public string IconColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Icon Size", WidgetPropertyType.Number, "Icon size in px (0 = auto-scale with the widget)", 0)]
    public int IconSize { get; set; } = 0;

    [WidgetProperty("Icon Offset X", WidgetPropertyType.Number, "Horizontal shift of the icon in px (negative = left)", 0)]
    public int IconOffsetX { get; set; } = 0;

    [WidgetProperty("Icon Offset Y", WidgetPropertyType.Number, "Vertical shift of the icon in px (negative = up)", 0)]
    public int IconOffsetY { get; set; } = 0;

    // ── IWidgetIconGrab: the input module never needs to know this widget
    // type. The icon geometry (0.62f max ratio, 0.4f auto-size, 0.31f anchor)
    // lives exactly here, in the widget that draws the icon — Render, hit
    // testing, and grab-move math all derive from one helper.

    public bool IsPointOverIcon(float width, float height, float localX, float localY)
    {
        if (!ComputeIconGeometry(width, height, out var center, out float half))
            return false;

        float dx = localX - center.X;
        float dy = localY - center.Y;
        return dx * dx + dy * dy <= half * half;
    }

    public bool TryGetIconCenter(float width, float height, out SKPoint center, out float half)
        => ComputeIconGeometry(width, height, out center, out half);

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
            : !string.IsNullOrWhiteSpace(Icon) && GriddyIcons.Contains(Icon);
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

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor btnColor = ColorOf(ButtonColorHex, new SKColor(135, 0, 0));
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor iconColor = ColorOf(IconColorHex, SKColors.White);

        if (_isPressed)
        {
            using var fillPaint = new SKPaint
            {
                Color = btnColor.WithAlpha(180),
                IsAntialias = true
            };
            canvas.DrawRoundRect(bounds, 16f, 16f, fillPaint);
        }

        string label = ButtonLabel;

        bool useCustomFile = !string.IsNullOrWhiteSpace(IconFile);
        if (!ComputeIconGeometry(bounds.Width, bounds.Height, out var iconCenter, out float half))
        {
            if (useCustomFile && _lastIconErrorPath != IconFile)
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
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, textPaint);
        canvas.DrawTextWithFallback(label, bounds.MidX - textBounds.Width / 2f,
            bounds.Top + bounds.Height * 0.78f, font, textPaint);

        if (!string.IsNullOrWhiteSpace(Description))
        {
            var descriptionFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, labelSize * 0.6f));
            using var descriptionPaint = new SKPaint { Color = textColor.WithAlpha(180), IsAntialias = true };
            descriptionFont.MeasureText(Description, out var descriptionBounds, descriptionPaint);
            canvas.DrawTextWithFallback(Description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(8f, labelSize * 0.4f), descriptionFont, descriptionPaint);
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
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };

        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, textPaint);
        canvas.DrawTextWithFallback(label, bounds.MidX - textBounds.Width / 2f, bounds.MidY - textBounds.Height / 4f, font, textPaint);

        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, fontSize * 0.42f));
            using var descriptionPaint = new SKPaint { Color = textColor.WithAlpha(180), IsAntialias = true };
            descriptionFont.MeasureText(description, out var descriptionBounds, descriptionPaint);
            canvas.DrawTextWithFallback(description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(12f, fontSize * 0.65f), descriptionFont, descriptionPaint);
        }
    }

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
            var action = CreateAction(ActionType, ActionCommand);
            if (string.IsNullOrWhiteSpace(action.Value) && IsLaunchOrUrlAction(ActionType))
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

    internal static HotkeyAction CreateAction(string actionType, string actionCommand)
        => actionType switch
        {
            "Open URL" => new HotkeyAction { Kind = HotkeyActionKind.OpenUrl, Value = actionCommand },
            "Media Play / Pause" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.PlayPause },
            "Media Next" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.Next },
            "Media Previous" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.Previous },
            "Media Stop" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.Stop },
            "Volume Up" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.VolumeUp },
            "Volume Down" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.VolumeDown },
            "Mute" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = MediaKeyCatalog.Mute },
            _ => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = actionCommand }
        };

    /// <summary>
    /// Single source of truth for 'action needs a command value' (Launch/URL).
    /// The inspector panel and the executor both consult this instead of
    /// re-listing action-type strings.
    /// </summary>
    public static bool IsLaunchOrUrlAction(string actionType)
        => actionType is "Launch App" or "Open URL";

    // ── IWidgetEditorProvider: special inspector editors ────────────────────
    // The inspector renderer discovers these through the interface instead of
    // branching on the widget type (no concrete-widget typeof checks).

    public EditorKind? GetEditorKind(PropertyInfo property)
    {
        if (property.Name == nameof(IconFile)) return EditorKind.IconPicker;
        if (property.Name == nameof(ActionCommand)) return EditorKind.ActionCommand;
        return null;
    }

    public PropertyInfo? GetIconFileCompanion(PropertyInfo iconProperty)
        => iconProperty.Name == nameof(Icon)
            ? typeof(HotkeyButtonWidget).GetProperty(nameof(IconFile))
            : null;

    public string? ActionCommandVisibilityChoicePropertyName => nameof(ActionType);

    public bool IsActionCommandVisible(string? actionTypeValue)
        => actionTypeValue != null && IsLaunchOrUrlAction(actionTypeValue);


    public override async ValueTask DisposeAsync()
    {
        if (_actionCts is { } cts)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        _actionGate.Dispose();
        await base.DisposeAsync();
    }
}
