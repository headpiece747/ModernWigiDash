namespace ModernWigiDash.Tests;

[TestClass]
public class RetiredBitmapSetTests
{
    [TestMethod]
    public void Retire_ThenDisposeRetired_DrainsPendingToZero()
    {
        var set = new RetiredBitmapSet();
        set.Retire(new SKBitmap(4, 4));

        Assert.AreEqual(1, set.PendingCount);

        set.DisposeRetired();

        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void Retire_Null_IsNoOp()
    {
        var set = new RetiredBitmapSet();

        set.Retire(null);

        Assert.AreEqual(0, set.PendingCount);
        set.DisposeRetired();
        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void RetireAll_WithNullsMixedIn_KeepsOnlyNonNull()
    {
        var set = new RetiredBitmapSet();

        set.RetireAll([null, new SKBitmap(2, 2), null, new SKBitmap(3, 3)]);

        Assert.AreEqual(2, set.PendingCount);

        set.DisposeRetired();

        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void DisposeRetired_EmptyAndTwice_IsSafe()
    {
        var set = new RetiredBitmapSet();

        set.DisposeRetired();
        set.DisposeRetired();
        set.Retire(new SKBitmap(2, 2));
        set.DisposeRetired();
        set.DisposeRetired();

        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void DisposeAll_DrainsAndDisposes()
    {
        var set = new RetiredBitmapSet();
        set.Retire(new SKBitmap(2, 2));
        set.Retire(new SKBitmap(3, 3));

        set.DisposeAll();

        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void ConcurrentRetire_ThenDrain_DoesNotThrow()
    {
        var set = new RetiredBitmapSet();
        var tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                    set.Retire(new SKBitmap(1, 1));
            });
        }

        Task.WaitAll(tasks);

        set.DisposeRetired();

        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void RetireAfterDrain_StaysPendingForNextDrain()
    {
        var set = new RetiredBitmapSet();
        set.Retire(new SKBitmap(2, 2));
        set.DisposeRetired();

        Assert.AreEqual(0, set.PendingCount);

        // A bitmap retired after a drain must not be swallowed by it: the
        // drain swaps the pending list, so the next drain disposes it.
        set.Retire(new SKBitmap(3, 3));
        Assert.AreEqual(1, set.PendingCount);

        set.DisposeRetired();
        Assert.AreEqual(0, set.PendingCount);
    }

    [TestMethod]
    public void NowPlayingWidget_RenderAndDispose_ExercisesRetirementWithoutThrowing()
    {
        using var surface = SKSurface.Create(new SKImageInfo(508, 296));
        var widget = new NowPlayingWidget();

        widget.Render(surface.Canvas, new SKRect(0, 0, 508, 296));

        // The idle state paints a solid background panel.
        Assert.AreNotEqual(SKColors.Transparent, surface.PeekPixels().GetPixelColor(254, 148));
        Assert.IsTrue(widget.DisposeAsync().AsTask().IsCompletedSuccessfully);
    }

    [TestMethod]
    public void PictureAndGifWidget_RenderAndDispose_ExercisesRetirementWithoutThrowing()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var widget = new PictureAndGifWidget();

        widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));

        Assert.IsTrue(widget.DisposeAsync().AsTask().IsCompletedSuccessfully);
    }
}
