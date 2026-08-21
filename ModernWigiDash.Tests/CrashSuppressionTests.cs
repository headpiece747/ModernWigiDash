namespace ModernWigiDash.Tests;

/// <summary>
/// The crash-suppression truth table (CrashSuppression.ShouldSuppress): which
/// exceptions the app's unhandled-exception handler swallows instead of
/// propagating. OCEs are expected during teardown (the frame pump, telemetry,
/// and presenter disposes raise them on close) — a cancelled token or the
/// closing flag makes one benign; anything else must crash loudly.
/// </summary>
[TestClass]
public class CrashSuppressionTests
{
    private static OperationCanceledException CancelledOce()
        => new("operation cancelled", new CancellationToken(canceled: true));

    private static OperationCanceledException NonCancelledOce()
        => new("operation cancelled", new CancellationToken(canceled: false));

    [TestMethod]
    public void ShouldSuppress_OceWithCancelledToken_Open_Suppresses()
    {
        Assert.IsTrue(CrashSuppression.ShouldSuppress(CancelledOce(), isClosing: false));
    }

    [TestMethod]
    public void ShouldSuppress_OceWithCancelledToken_Closing_Suppresses()
    {
        Assert.IsTrue(CrashSuppression.ShouldSuppress(CancelledOce(), isClosing: true));
    }

    [TestMethod]
    public void ShouldSuppress_OceWithoutCancelledToken_Closing_Suppresses()
    {
        Assert.IsTrue(CrashSuppression.ShouldSuppress(NonCancelledOce(), isClosing: true));
    }

    [TestMethod]
    public void ShouldSuppress_OceWithoutCancelledToken_Open_Propagates()
    {
        Assert.IsFalse(CrashSuppression.ShouldSuppress(NonCancelledOce(), isClosing: false),
            "an OCE whose token was never cancelled must not be swallowed while the app runs");
    }

    [TestMethod]
    public void ShouldSuppress_NonOce_AlwaysPropagates()
    {
        var exception = new InvalidOperationException("unexpected");

        Assert.IsFalse(CrashSuppression.ShouldSuppress(exception, isClosing: false));
        Assert.IsFalse(CrashSuppression.ShouldSuppress(exception, isClosing: true));
    }
}
