using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Owns the edit-mode authoring chrome: page grid, selection box, name/Z badge,
/// and resize handle. Rendering logic that used to live inside the compositor —
/// one module for the authoring overlay, one place to test it.
/// </summary>
public sealed class EditOverlay
{
    /// <summary>
    /// Size of the edit-mode resize handle, in canvas pixels. Single source of
    /// truth for the affordance: drawn here, hit-tested by the App's
    /// <c>InputController</c> against the compositor's forwarding constant.
    /// </summary>
    public const float ResizeHandleSize = 14f;

    // Borrowed process-lifetime typeface — never disposed here.
    private readonly SKTypeface _uiTypeface = FontHelper.GeistTypeface;

    // Fixed-color paints are created once and reused across composes instead
    // of allocated per draw (~10 SKPaints per frame in edit mode). The overlay
    // is owned by the compositor for the app lifetime, so these live for the
    // process too (same policy as the borrowed typeface and FontHelper's
    // caches) — no IDisposable.
    private readonly SKPaint _gridPaint = new()
    {
        Color = new SKColor(255, 255, 255, 12),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f
    };
    private readonly SKPaint _selectionPaint = new()
    {
        Color = new SKColor(59, 130, 246), // #3B82F6 vibrant blue
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2.5f,
        IsAntialias = true
    };
    private readonly SKPaint _badgeBackgroundPaint = new() { Color = new SKColor(59, 130, 246, 220) };
    private readonly SKPaint _badgeTextPaint = new()
    {
        Color = SKColors.White,
        IsAntialias = true
    };
    private readonly SKPaint _handlePaint = new()
    {
        Color = new SKColor(59, 130, 246, 200),
        Style = SKPaintStyle.Fill,
        IsAntialias = true
    };
    private readonly SKPaint _handleStrokePaint = new()
    {
        Color = SKColors.White,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1.5f,
        IsAntialias = true
    };

    /// <summary>
    /// Draws the snap-to-grid lines when edit mode is on and the page snaps.
    /// Called once per compose, before the widgets render.
    /// </summary>
    public void DrawGrid(SKCanvas canvas, PageLayout page, bool editMode)
    {
        if (!editMode || !page.SnapToGrid) return;

        // Classic 5x4 cell grid lines or custom spacing. CellWidth is 203.2 px
        // and the loop steps by (int)CellWidth = 203, so the last interior line
        // lands at 1015 — the right/bottom edge lines are drawn explicitly or
        // the 1px strip along the edge would be bare.
        for (int x = 0; x < DisplayGeometry.FramebufferWidth; x += (int)GridSizeExtensions.CellWidth)
        {
            canvas.DrawLine(x, 0, x, DisplayGeometry.FramebufferHeight, _gridPaint);
        }
        canvas.DrawLine(DisplayGeometry.FramebufferWidth, 0, DisplayGeometry.FramebufferWidth, DisplayGeometry.FramebufferHeight, _gridPaint);
        for (int y = 0; y < DisplayGeometry.FramebufferHeight; y += (int)GridSizeExtensions.CellHeight)
        {
            canvas.DrawLine(0, y, DisplayGeometry.FramebufferWidth, y, _gridPaint);
        }
        canvas.DrawLine(0, DisplayGeometry.FramebufferHeight, DisplayGeometry.FramebufferWidth, DisplayGeometry.FramebufferHeight, _gridPaint);
    }

    /// <summary>
    /// Draws the selection bounding box, name/Z badge, and resize handle for a
    /// widget selected in edit mode. Must be called from the widget's local
    /// canvas space (after translate/rotate) — the chrome is widget-relative.
    /// </summary>
    public void DrawSelection(SKCanvas canvas, PlacedWidgetInstance widget, bool editMode, bool isSelected)
    {
        if (!editMode || !isSelected) return;

        var bounds = new SKRect(0, 0, widget.Width, widget.Height);
        canvas.DrawRect(bounds, _selectionPaint);

        // Draw ZIndex / Name badge at top left
        using var font = FontHelper.CreateFont(_uiTypeface, 12f);
        string badgeText = $"{widget.DisplayName} (Z: {widget.ZIndex})";
        var textBounds = new SKRect();
        font.MeasureText(badgeText, out textBounds, _badgeTextPaint);
        canvas.DrawRect(0, -20, textBounds.Width + 10, 20, _badgeBackgroundPaint);
        canvas.DrawTextWithFallback(badgeText, 5, -5, font, _badgeTextPaint);

        // Draw resize handle at bottom-right corner. The size is the
        // single source of truth for the edit-mode resize affordance —
        // the App's InputController hit-tests against this constant.
        float hs = ResizeHandleSize;
        canvas.DrawRect(bounds.Width - hs - 2, bounds.Height - hs - 2, hs, hs, _handlePaint);
        canvas.DrawRect(bounds.Width - hs - 2, bounds.Height - hs - 2, hs, hs, _handleStrokePaint);
    }
}
