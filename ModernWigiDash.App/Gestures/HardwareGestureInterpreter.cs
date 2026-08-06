using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.Gestures;

/// <summary>
/// Page-navigation action produced by <see cref="HardwareGestureInterpreter"/>.
/// </summary>
public enum GesturePageAction
{
    None,
    NextPage,
    PrevPage
}

/// <summary>
/// Result of feeding one hardware touch sample into the gesture interpreter.
/// </summary>
/// <param name="PageAction">Swipe/edge-tap navigation to apply, or <see cref="GesturePageAction.None"/>.</param>
/// <param name="RouteToWidgets">True when the sample should be forwarded to the widget compositor.</param>
/// <param name="X">Touch X in display coordinates.</param>
/// <param name="Y">Touch Y in display coordinates.</param>
/// <param name="WidgetTouchType">Touch type to route to widgets (Down/Move/Up mapping).</param>
public sealed record GestureOutcome(
    GesturePageAction PageAction,
    bool RouteToWidgets,
    float X,
    float Y,
    TouchEventType WidgetTouchType);

/// <summary>
/// Pure gesture state machine for hardware touch input on the WigiDash display.
/// Owns the swipe/edge-tap/page-switch thresholds and the Down→Move remapping so
/// both the USB-direct and WCF touch paths share one implementation.
///
/// Hardware protocol: the display reports Down(1) for initial contact AND
/// intermediate movement points, and Up(2) for release — a single physical tap
/// therefore yields one Down followed by repeated Up samples.
/// </summary>
public sealed class HardwareGestureInterpreter
{
    public const float SwipeThresholdX = 70f;
    public const float SwipeToleranceY = 80f;
    public const float TapThreshold = 30f;
    public const float EdgeLeftX = 60f;
    public const float EdgeRightX = 964f;
    public const float EdgeTopY = 200f;
    public const float EdgeBottomY = 400f;
    public const float MoveSensitivity = 0.5f;

    private bool _active;
    private float _startX;
    private float _startY;

    /// <summary>
    /// Feeds one hardware touch sample and returns the gesture decision.
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
            // Only record the start position on the first Down; the hardware
            // sends Down(1) for both contact and intermediate movement points.
            if (!_active)
            {
                _startX = x;
                _startY = y;
                _active = true;
            }

            bool moved = Math.Abs(_startX - x) > MoveSensitivity || Math.Abs(_startY - y) > MoveSensitivity;
            return new GestureOutcome(GesturePageAction.None, true, x, y,
                moved ? TouchEventType.TouchMove : TouchEventType.TouchDown);
        }

        if (type == TouchEventType.TouchUp)
        {
            if (!_active)
            {
                // The display reports the release state for more than one poll.
                // Ignore subsequent releases so one physical tap becomes one action.
                return new GestureOutcome(GesturePageAction.None, false, x, y, TouchEventType.TouchUp);
            }

            _active = false;
            float deltaX = x - _startX;
            float deltaY = y - _startY;

            if (pageCount > 1)
            {
                // Horizontal swipe with a tight vertical tolerance.
                if (Math.Abs(deltaX) > SwipeThresholdX && Math.Abs(deltaY) < SwipeToleranceY)
                {
                    if (deltaX < -SwipeThresholdX && activePageIndex < pageCount - 1)
                    {
                        return new GestureOutcome(GesturePageAction.NextPage, false, x, y, TouchEventType.TouchUp);
                    }

                    if (deltaX > SwipeThresholdX && activePageIndex > 0)
                    {
                        return new GestureOutcome(GesturePageAction.PrevPage, false, x, y, TouchEventType.TouchUp);
                    }
                }

                // Arrow-tap fallback: stationary tap near the left/right edges.
                if (Math.Abs(deltaX) < TapThreshold && Math.Abs(deltaY) < TapThreshold)
                {
                    if (x <= EdgeLeftX && y >= EdgeTopY && y <= EdgeBottomY && activePageIndex > 0)
                    {
                        return new GestureOutcome(GesturePageAction.PrevPage, false, x, y, TouchEventType.TouchUp);
                    }

                    if (x >= EdgeRightX && y >= EdgeTopY && y <= EdgeBottomY && activePageIndex < pageCount - 1)
                    {
                        return new GestureOutcome(GesturePageAction.NextPage, false, x, y, TouchEventType.TouchUp);
                    }
                }
            }

            return new GestureOutcome(GesturePageAction.None, true, x, y, TouchEventType.TouchUp);
        }

        return new GestureOutcome(GesturePageAction.None, true, x, y, TouchEventType.TouchMove);
    }

    /// <summary>
    /// Clears in-progress gesture state (e.g. when the active page changes mid-gesture).
    /// </summary>
    public void Reset()
    {
        _active = false;
    }
}
