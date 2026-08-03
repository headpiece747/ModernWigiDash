using SkiaSharp;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Rendering;

public class SkiaFrameCompositor : IDisposable
{
    private readonly SKBitmap _frameBuffer = new(1016, 592);
    private readonly SKTypeface _uiTypeface = FontHelper.GeistTypeface;
    private bool _isEditMode = true;
    private PlacedWidgetInstance? _selectedWidget;

    public SKBitmap FrameBuffer => _frameBuffer;
    public bool IsEditMode
    {
        get => _isEditMode;
        set => _isEditMode = value;
    }
    public PlacedWidgetInstance? SelectedWidget
    {
        get => _selectedWidget;
        set => _selectedWidget = value;
    }

    public void Compose(PageLayout page, float fpsTelemetry = 60.0f, int pageIndex = 0, int pageCount = 1)
    {
        using var canvas = new SKCanvas(_frameBuffer);
        
        // 1. Clear background with charcoal slate / page background color
        if (SKColor.TryParse(page.BackgroundHexColor, out var bgColor))
            canvas.Clear(bgColor);
        else
            canvas.Clear(new SKColor(27, 41, 48)); // #1B2930

        // 2. Draw Grid Lines if SnapToGrid and Edit Mode are enabled
        if (_isEditMode && page.SnapToGrid)
        {
            using var gridPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 12),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f
            };

            // Classic 5x4 cell grid lines or custom spacing
            for (int x = 0; x <= 1016; x += (int)GridSizeExtensions.CellWidth)
            {
                canvas.DrawLine(x, 0, x, 592, gridPaint);
            }
            for (int y = 0; y <= 592; y += (int)GridSizeExtensions.CellHeight)
            {
                canvas.DrawLine(0, y, 1016, y, gridPaint);
            }
        }

        // 3. Render all placed widgets sorted by ZIndex (low to high)
        var sortedWidgets = page.Widgets.OrderBy(w => w.ZIndex).ToList();
        foreach (var widget in sortedWidgets)
        {
            if (widget.ActiveInstance == null)
                continue;

            int saveCount = canvas.Save();
            try
            {
                // Translate canvas to widget coordinate
                canvas.Translate(widget.X, widget.Y);

                // Apply rotation around center of widget if any
                if (Math.Abs(widget.Rotation) > 0.01f)
                {
                    canvas.RotateDegrees(widget.Rotation, widget.Width / 2f, widget.Height / 2f);
                }

                // Apply opacity using layer or paint setting
                var bounds = new SKRect(0, 0, widget.Width, widget.Height);

                if (widget.Opacity < 0.99f)
                {
                    using var alphaPaint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(widget.Opacity * 255)) };
                    canvas.SaveLayer(alphaPaint);
                }

                // Render the widget content directly to Skia canvas
                widget.ActiveInstance.Render(canvas, bounds);

                if (widget.Opacity < 0.99f)
                {
                    canvas.Restore();
                }

                // If in Edit Mode, draw selection bounding box & handles on the selected widget
                if (_isEditMode && widget == _selectedWidget)
                {
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
                    canvas.DrawText(badgeText, 5, -5, SKTextAlign.Left, font, textPaint);

                    // Draw resize handle at bottom-right corner
                    using var handlePaint = new SKPaint
                    {
                        Color = new SKColor(59, 130, 246, 200),
                        Style = SKPaintStyle.Fill,
                        IsAntialias = true
                    };
                    float hs = 10f;
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
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }

    }

    public static PlacedWidgetInstance? HitTest(PageLayout page, float pointX, float pointY)
    {
        // Check top-most widgets first (highest ZIndex)
        var sortedDesc = page.Widgets.OrderByDescending(w => w.ZIndex);
        return sortedDesc.FirstOrDefault(w => w.ContainsPoint(pointX, pointY));
    }

    public static void RouteTouch(PageLayout page, float pointX, float pointY, TouchEventType eventType)
    {
        var target = HitTest(page, pointX, pointY);
        if (target?.ActiveInstance != null)
        {
            var localPoint = new SKPoint(pointX - target.X, pointY - target.Y);
            target.ActiveInstance.OnTouch(localPoint, eventType);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _frameBuffer.Dispose();
            _uiTypeface.Dispose();
        }
        _disposed = true;
    }
}
