using SkiaSharp;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Rendering;

public class SkiaFrameCompositor : IDisposable
{
    /// <summary>
    /// Size of the edit-mode resize handle, in canvas pixels. Single source of
    /// truth for the affordance: drawn by <see cref="EditOverlay"/>, hit-tested
    /// by the App's <c>InputController</c> against this constant. Forwarded so
    /// the value lives only in the overlay module.
    /// </summary>
    public const float ResizeHandleSize = EditOverlay.ResizeHandleSize;

    private readonly SKBitmap _frameBuffer = new(DisplayGeometry.FramebufferWidth, DisplayGeometry.FramebufferHeight);
    private readonly SKCanvas _canvas;
    private readonly EditOverlay _editOverlay = new();
    // Defaults OFF: the App's MainWindow syncs this from the Edit Mode checkbox
    // on startup (the checkbox default is checked, so the window re-asserts it
    // explicitly); a compositor alone must not assume authoring mode.
    private bool _isEditMode = false;
    private PlacedWidgetInstance? _selectedWidget;

    // Zero-alloc render path: the buffer never changes, so the canvas is
    // created once and reused per compose; the background parse is hoisted
    // (reparsed only when the page's hex changes); the alpha layer paint is
    // cached and re-colored per widget instead of allocated per frame.
    private readonly SKPaint _alphaPaint = new();
    private string? _lastBgHex;
    private SKColor _lastBgColor = SKColor.TryParse(PageLayout.DefaultBackgroundHexColor, out var initial) ? initial : new SKColor(18, 20, 29);

    public SkiaFrameCompositor()
    {
        _canvas = new SKCanvas(_frameBuffer);
    }

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

    public void Compose(PageLayout page)
    {
        SKCanvas canvas = _canvas;

        // 1. Clear background with charcoal slate / page background color
        // (the string parse is hoisted: reparse only when the hex changes).
        if (page.BackgroundHexColor != _lastBgHex)
        {
            _lastBgHex = page.BackgroundHexColor;
            _lastBgColor = SKColor.TryParse(page.BackgroundHexColor, out var parsed)
                ? parsed
                : SKColor.TryParse(PageLayout.DefaultBackgroundHexColor, out var fallback)
                    ? fallback
                    : new SKColor(18, 20, 29);
        }
        canvas.Clear(_lastBgColor);

        // 2. Draw Grid Lines if SnapToGrid and Edit Mode are enabled (the
        //    authoring chrome lives in EditOverlay)
        _editOverlay.DrawGrid(canvas, page, _isEditMode);
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
                    _alphaPaint.Color = new SKColor(255, 255, 255, (byte)(widget.Opacity * 255));
                    canvas.SaveLayer(_alphaPaint);
                }

                // Render the widget content directly to Skia canvas
                widget.ActiveInstance.Render(canvas, bounds);

                if (widget.Opacity < 0.99f)
                {
                    canvas.Restore();
                }

                // If in Edit Mode, draw the selection bounding box & handles on
                // the selected widget (authoring chrome lives in EditOverlay)
                _editOverlay.DrawSelection(canvas, widget, _isEditMode, widget == _selectedWidget);
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
            var localPoint = target.ToLocalPoint(pointX, pointY);
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
            _canvas.Dispose();
            _alphaPaint.Dispose();
        }
        _disposed = true;
    }
}
