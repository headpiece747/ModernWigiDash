namespace ModernWigiDash.App;

/// <summary>
/// The crash-suppression decision for the app's unhandled-exception hooks.
/// An <see cref="OperationCanceledException"/> is benign when it carries a
/// cancelled token — the operation was cancelled by design, wherever it lands —
/// or while the app is closing (teardown cancels in-flight work). Every other
/// exception propagates so a genuine crash stays visible.
/// </summary>
internal static class CrashSuppression
{
    public static bool ShouldSuppress(Exception ex, bool isClosing)
        => ex is OperationCanceledException oce && (isClosing || oce.CancellationToken.IsCancellationRequested);
}
