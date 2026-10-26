namespace ModernWigiDash.Widgets.Todo;

using SkiaSharp;
using ModernWigiDash.Sdk;

/// <summary>
/// Deep presentation and interaction canvas for the To-Do Companion widget.
/// Owns the frame geometry (memoized per input change, so a steady-state
/// tick reuses the record and allocates no geometry per frame), feeds that
/// one record to both the renderer and the gesture hit-test (the drawn rows
/// and the tap targets cannot drift apart), and dispatches touch to the
/// gesture state machine.
/// </summary>
internal sealed class CozyTodoCanvas : IDisposable
{
    private readonly CozyTodoRenderer _renderer = new();
    private readonly CozyTodoGestureState _gesture = new();

    private CozyTodoGeometry _geometry;
    private bool _geometryValid;
    private SKRect _bounds;
    private bool _minimalist;
    private IReadOnlyList<TodoItem> _tasks = [];
    private float _scrollY;

    /// <summary>Callback fired when a task completion toggle is triggered by a tap.</summary>
    public Action<string>? OnTaskToggled
    {
        get => _gesture.OnTaskToggled;
        set => _gesture.OnTaskToggled = value;
    }

    /// <summary>Callback fired when the feed button is tapped.</summary>
    public Action? OnFeedTapped
    {
        get => _gesture.OnFeedTapped;
        set => _gesture.OnFeedTapped = value;
    }

    /// <summary>Callback fired when the focus timer play/pause button is tapped.</summary>
    public Action? OnFocusToggled
    {
        get => _gesture.OnFocusToggled;
        set => _gesture.OnFocusToggled = value;
    }

    /// <summary>
    /// Renders the widget onto the Skia canvas from the frame geometry (the
    /// memo below), feeding the same record to the gesture hit-test.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        bool isMinimalist,
        TodoCompanionSnapshot snapshot,
        SKColor customAccent,
        long animationTickMs,
        DateTime nowUtc)
    {
        CozyTodoGeometry geometry = FrameGeometry(bounds, isMinimalist, snapshot.Tasks, _gesture.ScrollY);
        _gesture.SetFrameGeometry(geometry);
        _renderer.Render(canvas, bounds, geometry, snapshot.Tasks, snapshot.Companion, snapshot.Focus, customAccent, animationTickMs, nowUtc);
    }

    /// <summary>
    /// The frame geometry, recomputed only when the bounds, the display mode,
    /// the task list, or the scroll offset changes (a steady-state tick
    /// reuses the record, so the render tick allocates no geometry). A
    /// sub-pixel scroll is a steady state.
    /// </summary>
    private CozyTodoGeometry FrameGeometry(SKRect bounds, bool isMinimalist, IReadOnlyList<TodoItem> tasks, float scrollY)
    {
        if (_geometryValid
            && _bounds == bounds
            && _minimalist == isMinimalist
            && ReferenceEquals(_tasks, tasks)
            && Math.Abs(_scrollY - scrollY) < 1e-3f)
        {
            return _geometry;
        }

        _geometry = CozyTodoLayout.Compute(bounds, isMinimalist, tasks, scrollY);
        _bounds = bounds;
        _minimalist = isMinimalist;
        _tasks = tasks;
        _scrollY = scrollY;
        _geometryValid = true;
        return _geometry;
    }

    /// <summary>
    /// Test seam: resolves the hit action for a point synchronously against
    /// the canvas's own frame geometry (the draw's record, the gesture's hit
    /// test), so a test drives tap-to-action without the async action leg.
    /// Production touch goes through <see cref="OnTouch"/>. Precondition:
    /// a <c>Render</c> primes the geometry; an unprimed canvas (no draw has
    /// run) resolves every point to <see cref="CozyTodoHitAction.None"/> —
    /// nothing is drawn, so there is no tap target.
    /// </summary>
    internal CozyTodoHitAction HandleTapForTest(SKPoint point)
        => CozyTodoLayout.HitTest(_geometry, point);

    /// <summary>
    /// Dispatches incoming touch events to the internal gesture state machine.
    /// </summary>
    public void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        _gesture.Feed(localPoint, eventType);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _renderer.Dispose();
    }
}
