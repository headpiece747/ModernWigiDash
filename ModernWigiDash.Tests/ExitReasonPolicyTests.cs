namespace ModernWigiDash.Tests;

/// <summary>
/// The close-attribution rule: a cancelled close records nothing, a close that
/// stands keeps an already-recorded cause, and an unattributed standing close is
/// the user closing the window. Pinned here because the window only forwards the
/// event and the static is otherwise untested.
/// </summary>
[TestClass]
public sealed class ExitReasonPolicyTests
{
    [TestMethod]
    public void ForClose_CancelledClose_RecordsNothing()
    {
        Assert.IsNull(ExitReasonPolicy.ForClose(cancelled: true, recorded: null));
    }

    [TestMethod]
    public void ForClose_StandingCloseWithNoReason_IsTheUserClose()
    {
        Assert.AreEqual(ExitReasonPolicy.UserClose, ExitReasonPolicy.ForClose(cancelled: false, recorded: null));
    }

    [TestMethod]
    public void ForClose_StandingClose_KeepsAnEarlierReason()
    {
        Assert.AreEqual(ExitReasonPolicy.TrayQuit,
            ExitReasonPolicy.ForClose(cancelled: false, recorded: ExitReasonPolicy.TrayQuit));
    }

    [TestMethod]
    public void ForClose_CancelledClose_KeepsAnEarlierReason()
    {
        // A session-end reason recorded before a close that the intercept then
        // cancelled must survive: the next close is not a fresh attribution.
        Assert.AreEqual(ExitReasonPolicy.SessionEnd,
            ExitReasonPolicy.ForClose(cancelled: true, recorded: ExitReasonPolicy.SessionEnd));
    }
}
