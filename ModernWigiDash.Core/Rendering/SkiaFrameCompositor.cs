using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// Composites a page's placed widgets into the 1016x592 SKBitmap frame buffer
/// that the render tick pushes into frame delivery.
/// </summary>
public class SkiaFrameCompositor : IDisposable
{
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

    /// <summary>Creates the compositor and its one reused frame buffer and canvas.</summary>
    public SkiaFrameCompositor()
    {
        _canvas = new SKCanvas(_frameBuffer);
    }

    /// <summary>The composited frame buffer (the SKBitmap the encode seam reads).</summary>
    public SKBitmap FrameBuffer => _frameBuffer;
    /// <summary>Whether the edit overlay (grid, selection chrome) is drawn; the App syncs it from the Edit Mode checkbox.</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set => _isEditMode = value;
    }
    /// <summary>The placement selected in edit mode (drives the selection outline), or null.</summary>
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

    /// <summary>
    /// Composes the page into the frame buffer: the background, the edit
    /// overlay, then the placed widgets in ZIndex order.
    /// </summary>
    /// <param name="page">The page to compose into the frame buffer.</param>
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

    /// <summary>Releases the frame buffer, canvas, and cached paints.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the Skia surfaces when <paramref name="disposing"/> is true.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>; false from the finalizer path.</param>
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
