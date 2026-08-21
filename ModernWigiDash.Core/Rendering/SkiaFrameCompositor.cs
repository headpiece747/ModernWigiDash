using ModernWigiDash.Core.Models;

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
    private SKColor _lastBgColor = ParseDefaultBackground();

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

    /// <summary>The page-background fallback — parses the one shared default.</summary>
    private static SKColor ParseDefaultBackground()
        => SKColor.TryParse(PageLayout.DefaultBackgroundHexColor, out var fallback)
            ? fallback
            : new SKColor(18, 20, 29);

    public void Compose(PageLayout page)
    {
        SKCanvas canvas = _canvas;

        // Clear the page background (the string parse is hoisted: reparse only
        // when the hex changes).
        if (!string.Equals(page.BackgroundHexColor, _lastBgHex, StringComparison.Ordinal))
        {
            _lastBgHex = page.BackgroundHexColor;
            _lastBgColor = SKColor.TryParse(page.BackgroundHexColor, out var parsed)
                ? parsed
                : ParseDefaultBackground();
        }
        canvas.Clear(_lastBgColor);

        _editOverlay.DrawGrid(canvas, page, _isEditMode);

        // Render placed widgets by ZIndex (low to high). Zero-alloc fast path:
        // insertion sort on a stack-allocated index span for the common small
        // page (<= 32 widgets); LINQ fallback for oversized pages. RenderOne
        // is a plain method (no local function — a capturing local function
        // would allocate a closure object per compose).
        List<PlacedWidgetInstance> widgetList = page.Widgets;

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

    private void RenderOne(PlacedWidgetInstance widget)
    {
        if (widget.ActiveInstance == null)
            return;

        int saveCount = _canvas.Save();
        try
        {
            _canvas.Translate(widget.X, widget.Y);

            if (Math.Abs(widget.Rotation) > 0.01f)
            {
                _canvas.RotateDegrees(widget.Rotation, widget.Width / 2f, widget.Height / 2f);
            }

            var bounds = new SKRect(0, 0, widget.Width, widget.Height);

            if (widget.Opacity < 0.99f)
            {
                _alphaPaint.Color = new SKColor(255, 255, 255, (byte)(widget.Opacity * 255));
                _canvas.SaveLayer(_alphaPaint);
            }

            widget.ActiveInstance.Render(_canvas, bounds);

            if (widget.Opacity < 0.99f)
            {
                _canvas.Restore();
            }

            _editOverlay.DrawSelection(_canvas, widget, _isEditMode, widget == _selectedWidget);
        }
        finally
        {
            _canvas.RestoreToCount(saveCount);
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
