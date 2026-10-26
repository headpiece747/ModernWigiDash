namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The picture media session's pins: the empty-state read (no media installed),
/// the <c>Reset</c> teardown, and the <c>AdvanceCycle</c> guard (a folder with
/// no images has nothing to cycle).
/// </summary>
[TestClass]
public class PictureMediaSessionTests
{
    private static readonly FakeTimeProvider Clock = new();
    private static readonly Action<string> NoLog = _ => { };
    private static readonly Action NoRender = () => { };

    [TestMethod]
    public void CurrentFrame_NoMedia_ReturnsEmptyTuple()
    {
        var session = new PictureMediaSession(Clock, NoLog, NoRender);

        var (frames, still, frameIndex) = session.CurrentFrame();

        Assert.IsNull(frames, "no GIF frames when nothing is installed");
        Assert.IsNull(still, "no static bitmap when nothing is installed");
        Assert.AreEqual(0, frameIndex, "the frame index is zero in the empty state");
    }

    [TestMethod]
    public void Reset_ClearsTheInstalledMedia()
    {
        var session = new PictureMediaSession(Clock, NoLog, NoRender);

        // Simulate a prior load by checking the empty state, then reset.
        var (frames, still, _) = session.CurrentFrame();
        Assert.IsNull(frames);
        Assert.IsNull(still);

        session.Reset();

        var (framesAfter, stillAfter, frameIndexAfter) = session.CurrentFrame();
        Assert.IsNull(framesAfter, "Reset clears the GIF frames");
        Assert.IsNull(stillAfter, "Reset clears the static bitmap");
        Assert.AreEqual(0, frameIndexAfter, "Reset zeros the frame index");
    }

    [TestMethod]
    public void AdvanceCycle_NoFolderImages_ReturnsFalse()
    {
        var session = new PictureMediaSession(Clock, NoLog, NoRender);

        // No folder images are listed (the folder scan has not run or found nothing).
        bool advanced = session.AdvanceCycle("Folder", "C:\\nonexistent");

        Assert.IsFalse(advanced, "a folder with no images has nothing to cycle");
    }
}
