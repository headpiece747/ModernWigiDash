using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared log-value sanitizer (the geocoder's Location queries, the price
/// feeds' symbols): embedded newlines cannot inject fake log entries, and a
/// multi-megabyte value cannot write a multi-megabyte line. One tested rule
/// instead of the deleted per-module copies.
/// </summary>
[TestClass]
public class LogSanitizerTests
{
    [TestMethod]
    public void Sanitize_EmbeddedLineBreaks_AreFlattenedToSpaces()
    {
        // A CRLF pair flattens to two spaces (each break becomes one space) -
        // the value cannot inject a fake entry into the log line.
        Assert.AreEqual("line one  line two", LogSanitizer.Sanitize("line one\r\nline two"));
        Assert.AreEqual("a b", LogSanitizer.Sanitize("a\nb"));
        Assert.AreEqual("a b", LogSanitizer.Sanitize("a\rb"));
    }

    [TestMethod]
    public void Sanitize_ShortValue_IsKeptWhole()
    {
        Assert.AreEqual("hello", LogSanitizer.Sanitize("hello"));
        Assert.AreEqual("", LogSanitizer.Sanitize(""));
    }

    [TestMethod]
    public void Sanitize_OverMaxLength_IsTruncatedAtTheBound()
    {
        var value = new string('x', LogSanitizer.MaxLogValueLength + 100);

        string result = LogSanitizer.Sanitize(value);

        Assert.AreEqual(LogSanitizer.MaxLogValueLength, result.Length);
        Assert.AreEqual(new string('x', LogSanitizer.MaxLogValueLength), result);
    }

    [TestMethod]
    public void Sanitize_ExactlyMaxLength_IsKeptWhole()
    {
        var value = new string('y', LogSanitizer.MaxLogValueLength);

        string result = LogSanitizer.Sanitize(value);

        Assert.AreEqual(value, result);
    }

    [TestMethod]
    public void Sanitize_NullValue_ReadsAsEmptyString()
    {
        // The sanitizer runs inside error paths over user-supplied input: a
        // null value must not replace the original failure with a secondary
        // NullReferenceException.
        Assert.AreEqual("", LogSanitizer.Sanitize(null));
    }

    [TestMethod]
    public void Sanitize_OverMaxLengthWithBreaks_FlattensAndTruncatesInOneBound()
    {
        // The single-pass scan must honor BOTH rules for one value: the breaks
        // inside the kept window are flattened, and the result never exceeds
        // the bound (a value that grows by flattening is still cut at the cap).
        var value = new string('a', LogSanitizer.MaxLogValueLength) + "\r\n" + new string('b', 50);

        string result = LogSanitizer.Sanitize(value);

        Assert.AreEqual(LogSanitizer.MaxLogValueLength, result.Length);
        Assert.AreEqual(new string('a', LogSanitizer.MaxLogValueLength), result);
    }
}
