using ModernWigiDash.App.Update;

namespace ModernWigiDash.Tests;

/// <summary>
/// The pure update-flow state machine — the check/download/failure
/// transitions, the click decision, and the one spelling of every tooltip —
/// pinned without WPF or network (the window's UpdateService I/O seams stay
/// the production I/O, exercised by UpdateServiceTests).
/// </summary>
[TestClass]
public class UpdateFlowTests
{
    private static UpdateInfo Info => new("1.2.3", "https://example/zip", "abc123");

    // ── startup check ─────────────────────────────────────────

    [TestMethod]
    public void CheckResult_NoUpdate_StaysHiddenSilent()
    {
        var flow = new UpdateFlow();

        var render = flow.CheckResult(null);

        Assert.IsNull(render, "up-to-date/offline/failed is silent — no render");
        Assert.AreEqual(UpdateState.Hidden, flow.State);
        Assert.IsNull(flow.PendingUpdate);
    }

    [TestMethod]
    public void CheckResult_UpdateFound_TransitionsToAvailableWithPending()
    {
        var flow = new UpdateFlow();
        var info = Info;

        var render = flow.CheckResult(info);

        Assert.IsNotNull(render);
        Assert.AreEqual(UpdateState.Available, flow.State);
        Assert.AreEqual(UpdateState.Available, render.State);
        Assert.AreEqual("Update v1.2.3 available", render.Tooltip);
        Assert.AreSame(info, flow.PendingUpdate);
    }

    // ── click decision ────────────────────────────────────────

    [TestMethod]
    public void OnClick_AvailableWithPending_ReturnsDownload()
    {
        var flow = new UpdateFlow();
        flow.CheckResult(Info);

        Assert.AreEqual(UpdateClickAction.Download, flow.OnClick());
    }

    [TestMethod]
    public void OnClick_Ready_ReturnsRestart()
    {
        var flow = new UpdateFlow();
        flow.CheckResult(Info);
        flow.DownloadComplete(Info, ok: true);

        Assert.AreEqual(UpdateClickAction.Restart, flow.OnClick());
    }

    [TestMethod]
    public void OnClick_Hidden_ReturnsNone()
    {
        var flow = new UpdateFlow();

        Assert.AreEqual(UpdateClickAction.None, flow.OnClick());
    }

    [TestMethod]
    public void OnClick_Downloading_ReturnsNone()
    {
        var flow = new UpdateFlow();
        flow.CheckResult(Info);
        flow.BeginDownload(Info);

        Assert.AreEqual(UpdateClickAction.None, flow.OnClick(),
            "the download is in flight — a click must not re-arm it");
    }

    // ── download outcome ──────────────────────────────────────

    [TestMethod]
    public void BeginDownload_ReturnsDownloadingStateWithZeroPercentTooltip()
    {
        var flow = new UpdateFlow();
        flow.CheckResult(Info);

        var render = flow.BeginDownload(Info);

        Assert.AreEqual(UpdateState.Downloading, flow.State);
        Assert.AreEqual("Downloading v1.2.3… 0%", render.Tooltip);
    }

    [TestMethod]
    public void DownloadSuccess_TriggersReadyWithPendingAndTooltip()
    {
        var flow = new UpdateFlow();
        var info = Info;
        flow.CheckResult(info);
        flow.BeginDownload(info);

        var render = flow.DownloadComplete(info, ok: true);

        Assert.AreEqual(UpdateState.Ready, flow.State);
        Assert.AreEqual("Restart to apply", render.Tooltip);
        Assert.AreSame(info, flow.PendingUpdate, "the pending update survives the hand-off to Ready");
    }

    [TestMethod]
    public void DownloadFailure_FallsBackToHiddenSilent()
    {
        var flow = new UpdateFlow();
        flow.CheckResult(Info);
        flow.BeginDownload(Info);

        var render = flow.DownloadComplete(Info, ok: false);

        Assert.AreEqual(UpdateState.Hidden, flow.State);
        Assert.AreEqual("", render.Tooltip, "the silent fail clears the tooltip");
    }

    [TestMethod]
    public void Fail_TransitionsToHidden()
    {
        var flow = new UpdateFlow();
        flow.CheckResult(Info);
        flow.DownloadComplete(Info, ok: true);

        var render = flow.Fail();

        Assert.AreEqual(UpdateState.Hidden, flow.State);
        Assert.AreEqual("", render.Tooltip);
    }

    // ── tooltip spellings ─────────────────────────────────────

    [TestMethod]
    public void AvailableTooltip_UsesExactSpelling()
        => Assert.AreEqual("Update v1.2.3 available", UpdateFlow.AvailableTooltip(Info));

    [TestMethod]
    public void DownloadingTooltip_RoundsToWholePercent()
    {
        Assert.AreEqual("Downloading v1.2.3… 0%", UpdateFlow.DownloadingTooltip(Info, 0.0));
        Assert.AreEqual("Downloading v1.2.3… 42%", UpdateFlow.DownloadingTooltip(Info, 0.424));
        Assert.AreEqual("Downloading v1.2.3… 100%", UpdateFlow.DownloadingTooltip(Info, 1.0));
    }

    [TestMethod]
    public void ReadyTooltip_UsesExactSpelling()
        => Assert.AreEqual("Restart to apply", UpdateFlow.ReadyTooltip);
}
