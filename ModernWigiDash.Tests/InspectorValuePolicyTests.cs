using System.Reflection;
using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.Tests;

[TestClass]
public class InspectorValuePolicyTests
{
    private sealed class TestWidget
    {
        public int Count { get; set; } = 0;
        public float Scale { get; set; } = 0f;
        public string Color { get; set; } = "#FFFFFF";
        public object? Anything { get; set; } = string.Empty;
    }

    private static readonly InspectorValuePolicy Policy = new();

    private static PropertyInfo Prop(string name) => typeof(TestWidget).GetProperty(name)!;

    [TestMethod]
    public void TryConvertStringToType_IntProperty_ConvertsAndFormatsBack()
    {
        Assert.IsTrue(Policy.TryConvertStringToType(Prop(nameof(TestWidget.Count)), "42", out object? value));

        Assert.AreEqual(42, value);
        Assert.AreEqual("42", Policy.FormatValue(value));
    }

    [TestMethod]
    public void TryConvertStringToType_FloatProperty_ConvertsAndFormatsBack()
    {
        Assert.IsTrue(Policy.TryConvertStringToType(Prop(nameof(TestWidget.Scale)), "12.5", out object? value));

        Assert.AreEqual(12.5f, value);
        Assert.AreEqual("12.5", Policy.FormatValue(value));
    }

    [TestMethod]
    public void TryConvertStringToType_ColorHexString_PassesThroughAndFormatsBack()
    {
        Assert.IsTrue(Policy.TryConvertStringToType(Prop(nameof(TestWidget.Color)), "#F59E0B", out object? value));

        Assert.AreEqual("#F59E0B", value);
        Assert.AreEqual("#F59E0B", Policy.FormatValue(value));
    }

    [TestMethod]
    public void TryConvertStringToType_GarbageForInt_ReturnsFalse()
    {
        Assert.IsFalse(Policy.TryConvertStringToType(Prop(nameof(TestWidget.Count)), "abc", out object? value));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryConvertStringToType_TypeWithoutConverter_ReturnsFalse()
    {
        Assert.IsFalse(Policy.TryConvertStringToType(Prop(nameof(TestWidget.Anything)), "x", out _));
    }

    [TestMethod]
    public void TryParsePosition_ValidNumber_ReturnsTrue()
    {
        Assert.IsTrue(Policy.TryParsePosition("42", out float x));
        Assert.AreEqual(42f, x);
    }

    [TestMethod]
    public void TryParsePosition_Garbage_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParsePosition("abc", out _));

    [TestMethod]
    public void TryParsePosition_EmptyText_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParsePosition("", out _));

    [TestMethod]
    public void TryParseSize_ValueAtMinimum_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParseSize("20", out _));

    [TestMethod]
    public void TryParseSize_ValueBelowMinimum_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParseSize("19", out _));

    [TestMethod]
    public void TryParseSize_ValueAboveMinimum_ReturnsTrue()
    {
        Assert.IsTrue(Policy.TryParseSize("21", out float w));
        Assert.AreEqual(21f, w);
    }

    [TestMethod]
    public void TryParseSize_Garbage_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParseSize("wide", out _));

    [TestMethod]
    public void TryParseRotation_ValidNumber_NormalizesToModulo360()
    {
        Assert.IsTrue(Policy.TryParseRotation("450", out float r));
        Assert.AreEqual(90f, r);
    }

    [TestMethod]
    public void TryParseRotation_NegativeNumber_KeepsNegativeRemainder()
    {
        Assert.IsTrue(Policy.TryParseRotation("-30", out float r));
        Assert.AreEqual(-30f, r);
    }

    [TestMethod]
    public void TryParseRotation_Garbage_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParseRotation("abc", out _));

    [TestMethod]
    public void TryParseZIndex_ValidNumber_ReturnsTrue()
    {
        Assert.IsTrue(Policy.TryParseZIndex("-5", out int z));
        Assert.AreEqual(-5, z);
    }

    [TestMethod]
    public void TryParseZIndex_Garbage_ReturnsFalse() =>
        Assert.IsFalse(Policy.TryParseZIndex("3.5", out _));

    [TestMethod]
    public void ClampOpacity_WithinRange_Unchanged() =>
        Assert.AreEqual(0.5f, Policy.ClampOpacity(0.5f));

    [TestMethod]
    public void ClampOpacity_BelowZero_ClampsToZero() =>
        Assert.AreEqual(0f, Policy.ClampOpacity(-0.1f));

    [TestMethod]
    public void ClampOpacity_AboveOne_ClampsToOne() =>
        Assert.AreEqual(1f, Policy.ClampOpacity(1.5f));

    [TestMethod]
    public void ClampOpacity_Boundaries_Unchanged()
    {
        Assert.AreEqual(0f, Policy.ClampOpacity(0f));
        Assert.AreEqual(1f, Policy.ClampOpacity(1f));
    }

    [TestMethod]
    public void FormatOpacityPercent_Value_ProducesTruncatedPercentLabel()
    {
        Assert.AreEqual("10%", Policy.FormatOpacityPercent(0.1f));
        Assert.AreEqual("99%", Policy.FormatOpacityPercent(0.999f));
        Assert.AreEqual("100%", Policy.FormatOpacityPercent(1f));
    }

    [TestMethod]
    public void FormatTransformValue_Value_RoundsToWholeNumber()
    {
        Assert.AreEqual("42", Policy.FormatTransformValue(42.4f));
        Assert.AreEqual("13", Policy.FormatTransformValue(12.7f));
    }

    [TestMethod]
    public void FormatValue_Null_ReturnsEmpty() =>
        Assert.AreEqual("", Policy.FormatValue(null));
}
