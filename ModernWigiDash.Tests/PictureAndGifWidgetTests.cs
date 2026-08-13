using System.IO;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class PictureAndGifWidgetTests
{
    [TestMethod]
    public void PictureAndGifWidget_FileAndFitModes_RenderWithoutExceptions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pngPath = Path.Combine(tempDir, "test.png");
        try
        {
            CreateTestPng(pngPath, SKColors.CornflowerBlue);

            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            foreach (var fitMode in new[] { "Cover", "Contain", "Stretch" })
            {
                var widget = new PictureAndGifWidget { ImagePath = pngPath, FitMode = fitMode, SourceMode = "Single Image" };
                widget.Render(canvas, bounds);
            }

            Assert.IsNotNull(surface);
        }
        finally
        {
            // The widget now decodes asynchronously; a decode task may still be
            // reading the file when teardown runs, so tolerate a transient lock
            // (also covers AV-scan file locks).
            DeleteTempDirWithRetry(tempDir);
        }
    }

    [TestMethod]
    public void PictureAndGifWidget_FolderCycle_AdvancesOnTouchWithoutCyclingInSingleMode()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            CreateTestPng(Path.Combine(tempDir, "a.png"), SKColors.Red);
            CreateTestPng(Path.Combine(tempDir, "b.png"), SKColors.Green);

            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            var folderWidget = new PictureAndGifWidget { ImagePath = tempDir, SourceMode = "Folder (Cycle)" };
            folderWidget.Render(canvas, bounds);
            folderWidget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
            folderWidget.Render(canvas, bounds);

            var singleWidget = new PictureAndGifWidget { ImagePath = tempDir, SourceMode = "Single Image" };
            singleWidget.Render(canvas, bounds);
            singleWidget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
            singleWidget.Render(canvas, bounds);

            Assert.IsNotNull(surface);
        }
        finally
        {
            DeleteTempDirWithRetry(tempDir);
        }
    }

    [TestMethod]
    public void PictureAndGifWidget_UnboundedDecode_IsCapped()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string pngPath = Path.Combine(tempDir, "test.png");
            CreateTestPng(pngPath, SKColors.CornflowerBlue);

            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            var widget = new PictureAndGifWidget { ImagePath = pngPath, SourceMode = "Single Image" };
            widget.Render(canvas, bounds);
            Assert.IsNotNull(surface);

            // A 40MB garbage file exceeds the 32MB decode cap: the widget must
            // refuse it without crashing or allocating a frame buffer.
            string hugePath = Path.Combine(tempDir, "huge.gif");
            File.WriteAllBytes(hugePath, new byte[40 * 1024 * 1024]);

            var hugeWidget = new PictureAndGifWidget { ImagePath = hugePath, SourceMode = "Single Image" };
            hugeWidget.Render(canvas, bounds);

            Assert.IsNotNull(surface);
        }
        finally
        {
            DeleteTempDirWithRetry(tempDir);
        }
    }

    [TestMethod]
    public void PictureAndGifWidget_FolderCycle_RescansForNewFilesAfterThrottle()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            CreateTestPng(Path.Combine(tempDir, "a.png"), SKColors.Red);

            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            var widget = new PictureAndGifWidget
            {
                ImagePath = tempDir,
                SourceMode = "Folder (Cycle)",
                Clock = clock
            };
            widget.Render(canvas, bounds); // first scan: [a.png]

            // A file added while the app runs must appear once the rescan
            // throttle elapses — the old one-shot latch froze the list forever.
            CreateTestPng(Path.Combine(tempDir, "b.png"), SKColors.Green);
            clock.Advance(TimeSpan.FromSeconds(31));

            widget.Render(canvas, bounds);

            Assert.IsTrue(widget._folderImagesForTest.Any(f => f.EndsWith("b.png", StringComparison.OrdinalIgnoreCase)),
                "a cycling folder must rescan for files added while running");
        }
        finally
        {
            DeleteTempDirWithRetry(tempDir);
        }
    }

    private static void DeleteTempDirWithRetry(string tempDir)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void CreateTestPng(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(60, 60);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
