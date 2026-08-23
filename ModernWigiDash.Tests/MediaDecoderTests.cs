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

        try
        {
            Assert.IsTrue(result.IsAnimated, "the 2-frame delta GIF must decode as an animation");
            Assert.AreEqual(2, result.Frames!.Length);
            Assert.AreEqual(100L, result.Durations![0]);
            Assert.AreEqual(200L, result.Durations[1]);

            SKBitmap frame0 = result.Frames[0];
            SKBitmap frame1 = result.Frames[1];
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

        try
        {
            Assert.IsTrue(result.IsAnimated);
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

        try
        {
            Assert.IsTrue(result.IsStill, "a single-frame image must decode as a still");
            Assert.IsNull(result.Frames);
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
        // DisposeMedia; the empty result must be a no-op, not a throw, and
        // it must leave the shared empty outcome empty.
        MediaDecodeResult.None.DisposeMedia();

        Assert.IsFalse(MediaDecodeResult.None.IsAnimated,
            "disposing the empty result leaves it frameless");
        Assert.IsFalse(MediaDecodeResult.None.IsStill,
            "disposing the empty result leaves it still-less");
    }

    [TestMethod]
    public void Decode_StillOverThePixelCap_RefusesWithALogLine()
    {
        // 5000x5000 (25 megapixels) exceeds the 4096x4096 per-frame pixel cap.
        // A solid-color PNG stays far under the file-byte cap, so this
        // isolates the still-image pixel clause.
        List<string> refusals = [];
        byte[] bigPng = CreatePngBytes(SKColors.Red, 5000, 5000);

        var result = MediaDecoder.Decode(bigPng, refusals.Add);

        Assert.AreEqual(MediaDecodeResult.None, result, "a still over the pixel cap must refuse");
        Assert.AreEqual(1, refusals.Count);
        Assert.IsTrue(refusals[0].Contains("still image"), "the refusal must name the still-image pixel cap");
    }

    [TestMethod]
    public void Decode_AnimatedOverThePixelCap_RefusesWithALogLine()
    {
        // solid.gif (2 frames) with the logical screen descriptor patched to
        // 4500x4500: 20.25 megapixels exceeds the per-frame pixel cap while
        // the total (162 MB) stays under the 512 MB total-frame-bytes cap, so
        // the animated pixel clause is the one that fires.
        List<string> refusals = [];

        var result = MediaDecoder.Decode(PatchedLsd(ReadFixture("solid.gif"), 4500), refusals.Add);

        Assert.AreEqual(MediaDecodeResult.None, result, "an animation over the pixel cap must refuse");
        Assert.AreEqual(1, refusals.Count);
        Assert.IsTrue(refusals[0].Contains("decode"), "the refusal must name the decode caps");
    }

    [TestMethod]
    public void Decode_AnimatedOverTheTotalBytesCap_RefusesWithALogLine()
    {
        // A 30-frame variant of toomanyframes (under the 256-frame cap) with
        // the logical screen descriptor patched to 4096x4096: the per-frame
        // pixel count sits exactly at its cap (the clause is strictly
        // greater-than) while the decoded total (1.875 GB) exceeds the 512 MB
        // total-frame-bytes cap - the clause a hostile small file can only
        // trip through decoded size.
        List<string> refusals = [];

        var result = MediaDecoder.Decode(RepeatedFrames(ReadFixture("toomanyframes.gif"), 30, 4096), refusals.Add);

        Assert.AreEqual(MediaDecodeResult.None, result, "an animation over the total-bytes cap must refuse");
        Assert.AreEqual(1, refusals.Count);
        Assert.IsTrue(refusals[0].Contains("decode"), "the refusal must name the decode caps");
    }

    /// <summary>
    /// Patches the GIF logical screen descriptor (the width/height pair at
    /// offset 6, little-endian 16-bit) on a fixture copy. The cap check reads
    /// the codec's declared size before any frame pixels decode, so the
    /// declared size drives the refusal without real pixels.
    /// </summary>
    private static byte[] PatchedLsd(byte[] gif, int size)
    {
        byte[] copy = (byte[])gif.Clone();
        copy[6] = (byte)(size & 0xFF);
        copy[7] = (byte)(size >> 8);
        copy[8] = copy[6];
        copy[9] = copy[7];
        return copy;
    }

    /// <summary>
    /// Repeats the fixture's uniform frame block (between the second and
    /// third graphics-control markers; the first block can differ in length -
    /// toomanyframes is 30 bytes vs 42) to the given frame count and patches
    /// the logical screen descriptor. A fixture that does not repeat cleanly
    /// fails loudly instead of building a malformed decode input.
    /// </summary>
    private static byte[] RepeatedFrames(byte[] gif, int frameCount, int lsdSize)
    {
        int first = IndexOf(gif, 0x21, 0xF9, 13);
        int second = IndexOf(gif, 0x21, 0xF9, first + 1);
        int third = IndexOf(gif, 0x21, 0xF9, second + 1);
        Assert.IsTrue(first >= 0 && second > first && third > second,
            "the fixture must carry at least three graphics-control markers");
        int blockLength = third - second;
        int bodyLength = gif.Length - second - 1; // -1: the 0x3B trailer
        Assert.IsTrue(bodyLength % blockLength == 0,
            "the fixture's uniform frame blocks must repeat cleanly");

        var result = new List<byte>(gif.AsSpan(0, second).ToArray());
        for (int i = 0; i < frameCount; i++)
        {
            result.AddRange(gif.AsSpan(second, blockLength).ToArray());
        }
        result.Add(0x3B);
        byte[] gifBytes = result.ToArray();
        return PatchedLsd(gifBytes, lsdSize);
    }

    private static int IndexOf(byte[] data, byte a, byte b, int start)
    {
        for (int i = start; i < data.Length - 1; i++)
        {
            if (data[i] == a && data[i + 1] == b)
            {
                return i;
            }
        }
        return -1;
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
