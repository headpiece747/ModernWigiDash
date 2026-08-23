using System.IO;

namespace ModernWigiDash.Tests;

[TestClass]
public class MediaDecoderTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "media-fixtures", name);

    private static byte[] ReadFixture(string name) => File.ReadAllBytes(FixturePath(name));

    /// <summary>
    /// delta.gif: two 8x8 frames, 100ms/200ms. Frame 0 is solid red; frame 1
    /// is a 4x8 partial delta at x=4 (turns blue, disposal: do-not-dispose),
    /// so the decode exercises the delta-composite path the heap-corruption
    /// guard around CopyTo/GetPixels exists for.
    /// </summary>
    [TestMethod]
    public void Decode_PartialDeltaGif_CompositesOverThePreviousFrame()
    {
        var result = MediaDecoder.Decode(ReadFixture("delta.gif"));

        Assert.IsTrue(result.IsAnimated, "the 2-frame delta GIF must decode as an animation");
        Assert.AreEqual(2, result.Frames!.Length);
        Assert.AreEqual(100L, result.Durations![0]);
        Assert.AreEqual(200L, result.Durations[1]);

        SKBitmap frame0 = result.Frames[0];
        SKBitmap frame1 = result.Frames[1];
        try
        {
            Assert.AreEqual(8, frame0.Width);
            Assert.AreEqual(8, frame0.Height);
            Assert.AreEqual(new SKColor(255, 0, 0), frame0.GetPixel(1, 1),
                "frame 0 is solid red");

            // The delta region (x>=4) is replaced by frame 1's blue column...
            Assert.AreEqual(new SKColor(0, 0, 255), frame1.GetPixel(6, 1),
                "the delta region must carry the new frame's blue pixels");
            // ...and everything outside it keeps the pixels composited from
            // the previous frame (a dangling-pixel read here is exactly the
            // 0xc0000374 trap the CopyTo-before-GetPixels order prevents).
            Assert.AreEqual(new SKColor(255, 0, 0), frame1.GetPixel(1, 1),
                "pixels outside the delta must composite from the previous frame");
        }
        finally
        {
            result.DisposeMedia();
        }
    }

    [TestMethod]
    public void Decode_FullFrameGif_ReplacesTheWholeFrame()
    {
        // solid.gif: two 8x8 full frames, red then blue. A full-frame "delta"
        // must replace every pixel, not blend with the previous frame.
        var result = MediaDecoder.Decode(ReadFixture("solid.gif"));

        Assert.IsTrue(result.IsAnimated);
        try
        {
            Assert.AreEqual(new SKColor(255, 0, 0), result.Frames![0].GetPixel(4, 4));
            Assert.AreEqual(new SKColor(0, 0, 255), result.Frames[1].GetPixel(0, 0),
                "the corner outside no delta must still be replaced by the full frame");
            Assert.AreEqual(new SKColor(0, 0, 255), result.Frames[1].GetPixel(7, 7));
        }
        finally
        {
            result.DisposeMedia();
        }
    }

    [TestMethod]
    public void Decode_SingleFramePng_DecodesTheStill()
    {
        // A still image (1 frame) takes the still path, not the animation path.
        byte[] png = CreatePngBytes(SKColors.CornflowerBlue, 60, 60);

        var result = MediaDecoder.Decode(png);

        Assert.IsTrue(result.IsStill, "a single-frame image must decode as a still");
        Assert.IsNull(result.Frames);
        try
        {
            Assert.AreEqual(60, result.Still!.Width);
            Assert.AreEqual(60, result.Still.Height);
            Assert.AreEqual(SKColors.CornflowerBlue, result.Still.GetPixel(5, 5));
        }
        finally
        {
            result.DisposeMedia();
        }
    }

    [TestMethod]
    public void Decode_GarbageBytes_ReturnsNoneWithoutThrowing()
    {
        byte[] garbage = [0x01, 0x02, 0xFF, 0x00, 0xDE, 0xAD, 0xBE, 0xEF];

        var result = MediaDecoder.Decode(garbage);

        Assert.AreEqual(MediaDecodeResult.None, result,
            "malformed media is an empty result, never an exception");
    }

    [TestMethod]
    public void Decode_OverTheFileByteCap_RefusesWithALogLine()
    {
        List<string> refusals = [];
        byte[] oversized = new byte[MediaDecoder.MaxFileBytes + 1];

        var result = MediaDecoder.Decode(oversized, refusals.Add);

        Assert.AreEqual(MediaDecodeResult.None, result, "over-cap media must refuse");
        Assert.AreEqual(1, refusals.Count);
        Assert.IsTrue(refusals[0].Contains("refusing"), "the refusal must carry the refusal facts");
    }

    [TestMethod]
    public void Decode_OverTheFrameCap_RefusesWithALogLine()
    {
        // toomanyframes.gif: 300 frames, ~12 KB raw (under the byte cap, over
        // the 256-frame cap) — a decoder that reads all frames before checking
        // the cap would allocate the whole buffer before refusing.
        List<string> refusals = [];

        var result = MediaDecoder.Decode(ReadFixture("toomanyframes.gif"), refusals.Add);

        Assert.AreEqual(MediaDecodeResult.None, result, "over-frame-cap media must refuse");
        Assert.AreEqual(1, refusals.Count);
        Assert.IsTrue(refusals[0].Contains("decode"), "the refusal must name the decode refusal");
    }

    [TestMethod]
    public void Decode_MissingLogCallback_StillDecodes()
    {
        // The log seam is optional: refusal facts are still enforced without it.
        var result = MediaDecoder.Decode(new byte[MediaDecoder.MaxFileBytes + 1]);

        Assert.AreEqual(MediaDecodeResult.None, result);
    }

    [TestMethod]
    public void DisposeMedia_OnTheEmptyResult_IsSafe()
    {
        // A discarded decode (stale version, path changed) routes through
        // DisposeMedia; the empty result must be a no-op, not a throw.
        MediaDecodeResult.None.DisposeMedia();

        Assert.AreEqual(MediaDecodeResult.None, MediaDecodeResult.None);
    }

    private static byte[] CreatePngBytes(SKColor color, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
