namespace ModernWigiDash.Tests;

[TestClass]
public class SparklineRendererTests
{
    private static readonly SKRect Area = new(50, 20, 350, 120);

    [TestMethod]
    public void BuildSparklinePaths_TwoOrMoreSamples_ProducesLineAndFillPaths()
    {
        double[] samples = [1.0, 2.0, 3.0, 4.0, 5.0];

        SparklineRenderer.BuildSparklinePaths(Area, samples, 0.0, 6.0, out SKPath? line, out SKPath? fill);

        Assert.IsNotNull(line);
        Assert.IsNotNull(fill);
        Assert.IsFalse(line.IsEmpty);
        Assert.IsFalse(fill.IsEmpty);
        Assert.IsTrue(line.Bounds.Left >= Area.Left);
        Assert.IsTrue(line.Bounds.Right <= Area.Right);
        Assert.IsTrue(line.Bounds.Top >= Area.Top);
        Assert.IsTrue(line.Bounds.Bottom <= Area.Bottom);
        line.Dispose();
        fill.Dispose();
    }

    [TestMethod]
    public void BuildSparklinePaths_FewerThanTwoSamples_ReturnsNullPaths()
    {
        double[] empty = [];
        SparklineRenderer.BuildSparklinePaths(Area, empty, 0.0, 1.0, out SKPath? emptyLine, out SKPath? emptyFill);
        Assert.IsNull(emptyLine);
        Assert.IsNull(emptyFill);

        double[] single = [3.5];
        SparklineRenderer.BuildSparklinePaths(Area, single, 0.0, 1.0, out SKPath? singleLine, out SKPath? singleFill);
        Assert.IsNull(singleLine);
        Assert.IsNull(singleFill);

        Span<float> emptySpan = [];
        SparklineRenderer.BuildSparklinePaths(Area, emptySpan, 0f, 1f, out SKPath? emptySpanLine, out SKPath? emptySpanFill);
        Assert.IsNull(emptySpanLine);
        Assert.IsNull(emptySpanFill);
    }

    [TestMethod]
    public void BuildSparklinePaths_FlatSamples_ExpandsBandAndStaysFinite()
    {
        float[] samples = [42f, 42f, 42f, 42f];

        SparklineRenderer.BuildSparklinePaths(Area, samples, 42f, 42f, out SKPath? line, out SKPath? fill);

        Assert.IsNotNull(line);
        Assert.IsNotNull(fill);
        SKRect bounds = line.Bounds;
        Assert.IsFalse(float.IsNaN(bounds.Left) || float.IsInfinity(bounds.Left));
        Assert.IsFalse(float.IsNaN(bounds.Top) || float.IsInfinity(bounds.Top));
        Assert.IsFalse(float.IsNaN(bounds.Right) || float.IsInfinity(bounds.Right));
        Assert.IsFalse(float.IsNaN(bounds.Bottom) || float.IsInfinity(bounds.Bottom));
        Assert.IsTrue(bounds.Top >= Area.Top);
        Assert.IsTrue(bounds.Bottom <= Area.Bottom);
        line.Dispose();
        fill.Dispose();
    }

    [TestMethod]
    public void BuildSparklinePaths_ListAndSpanOverloads_ProduceEquivalentGeometry()
    {
        double[] list = [10.5, 12.25, 9.75, 11.5, 10.0, 8.9, 13.1];
        Span<float> span = [10.5f, 12.25f, 9.75f, 11.5f, 10.0f, 8.9f, 13.1f];

        SparklineRenderer.BuildSparklinePaths(Area, list, 9.0, 13.0, out SKPath? listLine, out SKPath? listFill);
        SparklineRenderer.BuildSparklinePaths(Area, span, 9f, 13f, out SKPath? spanLine, out SKPath? spanFill);

        Assert.IsNotNull(listLine);
        Assert.IsNotNull(listFill);
        Assert.IsNotNull(spanLine);
        Assert.IsNotNull(spanFill);

        Assert.AreEqual(listLine.Bounds.Left, spanLine.Bounds.Left, 0.001);
        Assert.AreEqual(listLine.Bounds.Top, spanLine.Bounds.Top, 0.001);
        Assert.AreEqual(listLine.Bounds.Right, spanLine.Bounds.Right, 0.001);
        Assert.AreEqual(listLine.Bounds.Bottom, spanLine.Bounds.Bottom, 0.001);

        Assert.AreEqual(listFill.Bounds.Left, spanFill.Bounds.Left, 0.001);
        Assert.AreEqual(listFill.Bounds.Top, spanFill.Bounds.Top, 0.001);
        Assert.AreEqual(listFill.Bounds.Right, spanFill.Bounds.Right, 0.001);
        Assert.AreEqual(listFill.Bounds.Bottom, spanFill.Bounds.Bottom, 0.001);

        listLine.Dispose();
        listFill.Dispose();
        spanLine.Dispose();
        spanFill.Dispose();
    }

    [TestMethod]
    public void DrawSparkline_ListOverload_DrawsWithoutThrowing()
    {
        using var bitmap = new SKBitmap(400, 150);
        using var canvas = new SKCanvas(bitmap);
        double[] samples = [1.0, 2.0];

        SparklineRenderer.DrawSparkline(canvas, Area, samples, 0.0, 3.0, SKColors.Orange);

        Assert.IsNotNull(bitmap);
    }
}
