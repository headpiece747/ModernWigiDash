namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The full provider composite the widget drives: the task, focus-timer,
/// and companion faces owned by one instance. A provider that has only
/// some of the concepts implements only the faces it has; the widget's
/// factory seam asks for the composite, so the shipped providers implement
/// all three faces.
/// </summary>
public interface ITaskProvider : ITaskSource, IFocusTimerSource, ICompanionSource, IAsyncDisposable
{
}
