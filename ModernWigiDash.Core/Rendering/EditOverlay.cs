using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Owns the edit-mode authoring chrome: page grid, selection box, name/Z badge,
/// and resize handle. Rendering logic that used to live inside the compositor —
/// one module for the authoring overlay, one place to test it.
/// </summary>
public sealed class EditOverlay : IDisposable
{
    /// <summary>
    /// Size of the edit-mode resize handle, in canvas pixels. Single source of
    /// truth for the affordance: drawn here, hit-tested by the App's
    /// <c>InputController</c> against the compositor's forwarding constant.
    /// </summary>
    public const float ResizeHandleSize = 14f;

    // Borrowed process-lifetime typeface — never disposed here.
    private readonly SKTypeface _uiTypeface = FontHelper.GeistTypeface;

    /// <summary>
    /// Draws the snap-to-grid lines when edit mode is on and the page snaps.
    /// Called once per compose, before the widgets render.
    /// </summary>
    public void DrawGrid(SKCanvas canvas, PageLayout page, bool editMode)
    {
        if (!editMode || !page.SnapToGrid) return;

        using var gridPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 12),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };

        // Classic 5x4 cell grid lines or custom spacing
        for (int x = 0; x <= DisplayGeometry.FramebufferWidth; x += (int)GridSizeExtensions.CellWidth)
        {
            canvas.DrawLine(x, 0, x, DisplayGeometry.FramebufferHeight, gridPaint);
        }
        for (int y = 0; y <= DisplayGeometry.FramebufferHeight; y += (int)GridSizeExtensions.CellHeight)
        {
            canvas.DrawLine(0, y, DisplayGeometry.FramebufferWidth, y, gridPaint);
        }
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

        using var selectionPaint = new SKPaint
        {
            Color = new SKColor(59, 130, 246), // #3B82F6 vibrant blue
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            IsAntialias = true
        };
        canvas.DrawRect(bounds, selectionPaint);

        // Draw ZIndex / Name badge at top left
        using var badgeBg = new SKPaint { Color = new SKColor(59, 130, 246, 220) };
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = FontHelper.CreateFont(_uiTypeface, 12f);
        string badgeText = $"{widget.DisplayName} (Z: {widget.ZIndex})";
        var textBounds = new SKRect();
        font.MeasureText(badgeText, out textBounds, textPaint);
        canvas.DrawRect(0, -20, textBounds.Width + 10, 20, badgeBg);
        canvas.DrawTextWithFallback(badgeText, 5, -5, font, textPaint);

        // Draw resize handle at bottom-right corner. The size is the
        // single source of truth for the edit-mode resize affordance —
        // the App's InputController hit-tests against this constant.
        using var handlePaint = new SKPaint
        {
            Color = new SKColor(59, 130, 246, 200),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        float hs = ResizeHandleSize;
        canvas.DrawRect(bounds.Width - hs - 2, bounds.Height - hs - 2, hs, hs, handlePaint);
        using var handleStroke = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        canvas.DrawRect(bounds.Width - hs - 2, bounds.Height - hs - 2, hs, hs, handleStroke);
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // No owned native resources: _uiTypeface is a borrowed
            // process-lifetime typeface — never disposed here.
        }
        _disposed = true;
    }
}
