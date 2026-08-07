using SkiaSharp;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Rendering;

public class SkiaFrameCompositor : IDisposable
{
    /// <summary>
    /// Size of the edit-mode resize handle, in canvas pixels. Single source of
    /// truth for the affordance: drawn here, hit-tested by the App's
    /// <c>InputController</c> against this constant.
    /// </summary>
    public const float ResizeHandleSize = 14f;

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
        // 3. Render all placed widgets sorted by ZIndex (low to high).
        // Zero-alloc fast path: stack-allocated copy + insertion sort for the
        // common small page (<= 32 widgets); LINQ fallback for oversized pages.
        List<PlacedWidgetInstance> widgetList = page.Widgets;
        void RenderOne(PlacedWidgetInstance widget)
        {
            if (widget.ActiveInstance == null)
                return;

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
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }

        if (widgetList.Count <= 32)
        {
            // Sort indices by ZIndex on the stack (widgets are reference types,
            // so stackalloc holds int indices into the list instead).
            Span<int> order = stackalloc int[widgetList.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }
            InsertionSortByZIndex(order, widgetList);
            foreach (int index in order)
            {
                RenderOne(widgetList[index]);
            }
        }
        else
        {
            foreach (PlacedWidgetInstance widget in widgetList.OrderBy(w => w.ZIndex))
            {
                RenderOne(widget);
            }
        }

    }

    /// <summary>
    /// Stable insertion sort of widget indices by ZIndex (low to high).
    /// Widget counts per page are tiny, so quadratic worst case is fine and
    /// this stays fully allocation-free on the stack-allocated index span.
    /// </summary>
    private static void InsertionSortByZIndex(Span<int> order, List<PlacedWidgetInstance> widgets)
    {
        for (int i = 1; i < order.Length; i++)
        {
            int current = order[i];
            int j = i - 1;
            while (j >= 0 && widgets[order[j]].ZIndex > widgets[current].ZIndex)
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = current;
        }
    }

    public static PlacedWidgetInstance? HitTest(PageLayout page, float pointX, float pointY)
    {
        // Top-most widget (highest ZIndex) that contains the point — single
        // pass, zero allocation (replaces OrderByDescending+FirstOrDefault).
        PlacedWidgetInstance? best = null;
        foreach (PlacedWidgetInstance widget in page.Widgets)
        {
            if (!widget.ContainsPoint(pointX, pointY)) continue;
            if (best == null || widget.ZIndex > best.ZIndex)
            {
                best = widget;
            }
        }
        return best;
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
