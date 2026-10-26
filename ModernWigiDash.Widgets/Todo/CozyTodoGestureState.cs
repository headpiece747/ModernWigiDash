namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Touch state machine for the Cozy Companion task widget.
/// Interprets Down/Move/Up events, distinguishes taps from scrolling (TapDragTolerance = 15f),
/// supports multi-touch tracking with zero per-event allocations, and dispatches hit actions to widget callbacks.
/// </summary>
internal sealed class CozyTodoGestureState
{
    /// <summary>Tolerance radius in pixels distinguishing a tap from a drag.</summary>
    public const float TapDragTolerance = 15f;

    private struct ActiveTouch
    {
        public bool InUse;
        public SKPoint PressPoint;
        public float ScrollStart;
        public bool IsDragging;
    }

    private readonly Lock _lock = new();
    private readonly ActiveTouch[] _touches = new ActiveTouch[8];
    private CozyTodoGeometry _currentGeometry;

    /// <summary>Current vertical scroll position of the task list.</summary>
    public float ScrollY { get; private set; }

    /// <summary>Maximum scroll extent calculated from the last frame.</summary>
    public float MaxScrollY
    {
        get
        {
            lock (_lock)
            {
                return _currentGeometry.MaxScrollOffset;
            }
        }
    }

    /// <summary>Callback fired when a task's checkmark is tapped.</summary>
    public Action<string>? OnTaskToggled { get; set; }

    /// <summary>Callback fired when the feed pet button is tapped.</summary>
    public Action? OnFeedTapped { get; set; }

    /// <summary>Callback fired when the Pomodoro timer button is tapped.</summary>
    public Action? OnFocusToggled { get; set; }

    /// <summary>
    /// Updates the frame's geometry from the layout computation.
    /// </summary>
    public void SetFrameGeometry(CozyTodoGeometry geometry)
    {
        lock (_lock)
        {
            _currentGeometry = geometry;
            ScrollY = Math.Clamp(ScrollY, 0f, geometry.MaxScrollOffset);
        }
    }

    /// <summary>
    /// Feeds a compositor touch event into the state machine.
    /// </summary>
    public void Feed(SKPoint localPoint, TouchEventType eventType)
    {
        Action? tapAction = null;
        lock (_lock)
        {
            switch (eventType)
            {
                case TouchEventType.TouchDown:
                    int freeIdx = -1;
                    for (int i = 0; i < _touches.Length; i++)
                    {
                        if (!_touches[i].InUse)
                        {
                            freeIdx = i;
                            break;
                        }
                    }
                    if (freeIdx >= 0)
                    {
                        _touches[freeIdx] = new ActiveTouch
                        {
                            InUse = true,
                            PressPoint = localPoint,
                            ScrollStart = ScrollY,
                            IsDragging = false,
                        };
                    }
                    break;

                case TouchEventType.TouchMove:
                    int moveIdx = FindClosestTouch(localPoint);
                    if (moveIdx >= 0)
                    {
                        ref var touch = ref _touches[moveIdx];
                        float dy = touch.PressPoint.Y - localPoint.Y;
                        if (!touch.IsDragging && Math.Abs(dy) > TapDragTolerance)
                        {
                            touch.IsDragging = true;
                        }

                        if (touch.IsDragging)
                        {
                            ScrollY = Math.Clamp(touch.ScrollStart + dy, 0f, _currentGeometry.MaxScrollOffset);
                        }
                    }
                    break;

                case TouchEventType.TouchUp:
                    int upIdx = FindClosestTouch(localPoint);
                    if (upIdx >= 0)
                    {
                        var touch = _touches[upIdx];
                        _touches[upIdx].InUse = false;

                        if (!touch.IsDragging && SKPoint.Distance(touch.PressPoint, localPoint) <= TapDragTolerance)
                        {
                            tapAction = () => DispatchTap(localPoint);
                        }
                    }
                    break;
            }
        }
        tapAction?.Invoke();
    }

    private int FindClosestTouch(SKPoint point)
    {
        int closest = -1;
        float minDist = float.MaxValue;
        for (int i = 0; i < _touches.Length; i++)
        {
            if (_touches[i].InUse)
            {
                float d = SKPoint.Distance(_touches[i].PressPoint, point);
                if (d < minDist)
                {
                    minDist = d;
                    closest = i;
                }
            }
        }
        return closest;
    }

    private void DispatchTap(SKPoint point)
    {
        CozyTodoGeometry geom;
        lock (_lock)
        {
            geom = _currentGeometry;
        }

        var action = CozyTodoLayout.HitTest(geom, point);
        switch (action.Kind)
        {
            case CozyTodoHitKind.ToggleTask when action.TaskId is not null:
                OnTaskToggled?.Invoke(action.TaskId);
                break;

            case CozyTodoHitKind.FeedPet:
                OnFeedTapped?.Invoke();
                break;

            case CozyTodoHitKind.ToggleFocus:
                OnFocusToggled?.Invoke();
                break;
        }
    }
}
