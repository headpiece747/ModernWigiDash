using SkiaSharp;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Rendering;

public class SkiaFrameCompositor : IDisposable
{
    private readonly SKBitmap _frameBuffer = new(1024, 600);
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
        
        // 1. Clear background with dark slate / page background color
        if (SKColor.TryParse(page.BackgroundHexColor, out var bgColor))
            canvas.Clear(bgColor);
        else
            canvas.Clear(new SKColor(18, 20, 29)); // #12141D

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
            for (int x = 0; x <= 1024; x += (int)GridSizeExtensions.CellWidth)
            {
                canvas.DrawLine(x, 0, x, 600, gridPaint);
            }
            for (int y = 0; y <= 600; y += (int)GridSizeExtensions.CellHeight)
            {
                canvas.DrawLine(0, y, 1024, y, gridPaint);
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
                    using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 12f);
                    string badgeText = $"{widget.DisplayName} (Z: {widget.ZIndex})";
                    var textBounds = new SKRect();
                    font.MeasureText(badgeText, out textBounds, textPaint);
                    canvas.DrawRect(0, -20, textBounds.Width + 10, 20, badgeBg);
                    canvas.DrawText(badgeText, 5, -5, SKTextAlign.Left, font, textPaint);
                }
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }

        // 4. Render Page Navigation Controls & Page Dots if multiple pages exist
        if (pageCount > 1)
        {
            // Draw Previous Page Arrow (Left)
            if (pageIndex > 0)
            {
                using var arrowBg = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true };
                canvas.DrawCircle(24, 300, 20, arrowBg);
                using var arrowFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 18f);
                using var arrowPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                canvas.DrawText("◄", 17, 306, SKTextAlign.Left, arrowFont, arrowPaint);
            }

            // Draw Next Page Arrow (Right)
            if (pageIndex < pageCount - 1)
            {
                using var arrowBg = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true };
                canvas.DrawCircle(1000, 300, 20, arrowBg);
                using var arrowFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 18f);
                using var arrowPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                canvas.DrawText("►", 993, 306, SKTextAlign.Left, arrowFont, arrowPaint);
            }

            // Draw Bottom Page Dots
            float dotSpacing = 16f;
            float totalDotsWidth = (pageCount - 1) * dotSpacing;
            float startX = 512f - (totalDotsWidth / 2f);
            float dotY = 582f;

            for (int i = 0; i < pageCount; i++)
            {
                float dx = startX + (i * dotSpacing);
                bool isActive = (i == pageIndex);
                using var dotPaint = new SKPaint
                {
                    Color = isActive ? new SKColor(59, 130, 246) : new SKColor(255, 255, 255, 100),
                    IsAntialias = true
                };
                canvas.DrawCircle(dx, dotY, isActive ? 5f : 3.5f, dotPaint);
            }
        }
    }

    public PlacedWidgetInstance? HitTest(PageLayout page, float pointX, float pointY)
    {
        // Check top-most widgets first (highest ZIndex)
        var sortedDesc = page.Widgets.OrderByDescending(w => w.ZIndex);
        foreach (var widget in sortedDesc)
        {
            if (widget.ContainsPoint(pointX, pointY))
            {
                return widget;
            }
        }
        return null;
    }

    public void RouteTouch(PageLayout page, float pointX, float pointY, TouchEventType eventType)
    {
        var target = HitTest(page, pointX, pointY);
        if (target?.ActiveInstance != null)
        {
            var localPoint = new SKPoint(pointX - target.X, pointY - target.Y);
            target.ActiveInstance.OnTouch(localPoint, eventType);
        }
    }

    public void Dispose()
    {
        _frameBuffer.Dispose();
    }
}
