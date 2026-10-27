namespace ModernWigiDash.App.Gestures;

/// <summary>
/// Page-navigation action produced by <see cref="GestureInterpreter"/>.
/// </summary>
internal enum GesturePageAction
{
    None,
    NextPage,
    PrevPage
}

/// <summary>
/// Result of feeding one input sample into the gesture interpreter. A
/// readonly struct (the <see cref="ModernWigiDash.App.Input.InputState"/> precedent): callers only read
/// the three fields, so the per-sample outcome is a value, never a heap
/// allocation on the touch path.
/// </summary>
/// <param name="PageAction">Swipe navigation to apply, or <see cref="GesturePageAction.None"/>.</param>
/// <param name="RouteToWidgets">True when the sample should be forwarded to the widget compositor.</param>
/// <param name="WidgetTouchType">Touch type to route to widgets (Down/Move/Up mapping).</param>
internal readonly record struct GestureOutcome(
    GesturePageAction PageAction,
    bool RouteToWidgets,
    TouchEventType WidgetTouchType);

/// <summary>
/// Pure gesture state machine shared by every input source on the WigiDash
/// display. Owns the swipe thresholds and the Down->Move
/// remapping so the USB-direct touch path and the desktop
/// mouse all apply one canonical gesture vocabulary.
///
/// Hardware protocol: the display reports Down(1) for initial contact AND
/// intermediate movement points, and Up(2) for release -- a single physical tap
/// therefore yields one Down followed by repeated Up samples. The mouse feeds
/// the same normalized Down/Move/Up sequence.
/// </summary>
internal sealed class GestureInterpreter
{
    public const float SwipeThresholdX = 70f;
    public const float SwipeToleranceY = 80f;
    public const float MoveSensitivity = 0.5f;

    private bool _active;
    private float _startX;
    private float _startY;

    /// <summary>
    /// Feeds one input sample and returns the gesture decision.
    /// </summary>
    /// <param name="type">Normalized touch type (Down/Move/Up).</param>
    /// <param name="x">Touch X in display coordinates.</param>
    /// <param name="y">Touch Y in display coordinates.</param>
    /// <param name="pageCount">Total page count (bounds page navigation).</param>
    /// <param name="activePageIndex">Currently active page index (bounds page navigation).</param>
    public GestureOutcome Feed(TouchEventType type, float x, float y, int pageCount, int activePageIndex)
    {
        if (type == TouchEventType.TouchDown)
        {
            return HandleDown(x, y);
        }

        if (type == TouchEventType.TouchUp)
        {
            return HandleUp(x, y, pageCount, activePageIndex);
        }

        return new GestureOutcome(GesturePageAction.None, true, TouchEventType.TouchMove);
    }

    private GestureOutcome HandleDown(float x, float y)
    {
        // Only emit TouchDown on the initial contact; the hardware reports
        // Down(1) for both contact and all intermediate samples while the finger
        // remains on the display. Subsequent samples while active route as
        // TouchMove so widgets receive a canonical Down -> Move* -> Up sequence.
        if (!_active)
        {
            _startX = x;
            _startY = y;
            _active = true;
            return new GestureOutcome(GesturePageAction.None, true, TouchEventType.TouchDown);
        }

        return new GestureOutcome(GesturePageAction.None, true, TouchEventType.TouchMove);
    }

    private GestureOutcome HandleUp(float x, float y, int pageCount, int activePageIndex)
    {
        if (!_active)
        {
            // The display reports the release state for more than one poll.
            // Ignore subsequent releases so one physical tap becomes one action.
            return new GestureOutcome(GesturePageAction.None, false, TouchEventType.TouchUp);
        }

        _active = false;
        float deltaX = x - _startX;
        float deltaY = y - _startY;

        if (pageCount > 1)
        {
            GesturePageAction swipe = SwipeAction(deltaX, deltaY, pageCount, activePageIndex);
            if (swipe != GesturePageAction.None)
            {
                return new GestureOutcome(swipe, false, TouchEventType.TouchUp);
            }
        }

        return new GestureOutcome(GesturePageAction.None, true, TouchEventType.TouchUp);
    }

    /// <summary>Horizontal swipe with a tight vertical tolerance, bounded by the
    /// page index: NextPage for a left swipe on a non-last page, PrevPage for a
    /// right swipe on a non-first page.</summary>
    private static GesturePageAction SwipeAction(float deltaX, float deltaY, int pageCount, int activePageIndex)
    {
        if (Math.Abs(deltaX) <= SwipeThresholdX || Math.Abs(deltaY) >= SwipeToleranceY)
        {
            return GesturePageAction.None;
        }
        if (deltaX < -SwipeThresholdX && activePageIndex < pageCount - 1) return GesturePageAction.NextPage;
        if (deltaX > SwipeThresholdX && activePageIndex > 0) return GesturePageAction.PrevPage;
        return GesturePageAction.None;
    }
}
