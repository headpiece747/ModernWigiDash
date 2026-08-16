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
}
