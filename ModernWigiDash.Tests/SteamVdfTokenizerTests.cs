using ModernWigiDash.Widgets.Steam.Parsing;

namespace ModernWigiDash.Tests;

/// <summary>
/// The zero-allocation VDF scanner's parse rules: leaf key/value extraction,
/// multi-volume enumeration, VDF escape unescaping, non-standard spacing,
/// block-value skipping, and the corrupt/truncated-input grace rule (no
/// value past the fault, never a throw).
/// </summary>
[TestClass]
public sealed class SteamVdfTokenizerTests
{
    private const string MultiVolumeVdf = """
        "libraryfolders"
        {
            "1"
            {
                "path"    "C:\\Program Files (x86)\\Steam"
                "label"   "libraryfolder1"
            }
            "2"
            {
                "path"    "D:\\Games\\SteamLibrary"
                "label"   "games"
            }
            "3"
            {
                "path"    "E:\\Steam"
                "label"   "e"
            }
        }
        """;

    private const string AcfWithEscapes = """
        "AppState"
        {
            "appid"                 "730"
            "Universe"              "1"
            "name"                  "Counter-Strike \"Beta\""
            "StateFlags"            "4"
            "installdir"            "Counter-Strike Global\\ Offensive"
            "LastPlayed"            "1700000000"
            "BytesDownloaded"       "123456789"
            "BytesToDownload"       "0"
            "hours_on_record"       "1234.5"
        }
        """;

    [TestMethod]
    public void TryFindValue_ValidMultiVolumeLibraryFolders_EnumeratesEveryPath()
    {
        var paths = new List<string>();
        int index = 0;
        while (SteamVdfTokenizer.TryFindValue(MultiVolumeVdf.AsSpan(), "path", index, out int start, out int length))
        {
            paths.Add(SteamVdfTokenizer.Unescape(MultiVolumeVdf.AsSpan()[start..(start + length)]));
            index = start + length + 2;
        }

        CollectionAssert.AreEquivalent(
            new[]
            {
                "C:\\Program Files (x86)\\Steam",
                "D:\\Games\\SteamLibrary",
                "E:\\Steam",
            },
            paths);
    }

    [TestMethod]
    public void TryFindValue_EscapedAcfValues_UnescapesToTheCSharpSpelling()
    {
        ReadOnlySpan<char> span = AcfWithEscapes.AsSpan();

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "name", 0, out int nameStart, out int nameLength));
        Assert.AreEqual(@"Counter-Strike ""Beta""", SteamVdfTokenizer.Unescape(span[nameStart..(nameStart + nameLength)]));

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "installdir", 0, out int dirStart, out int dirLength));
        Assert.AreEqual("Counter-Strike Global\\ Offensive", SteamVdfTokenizer.Unescape(span[dirStart..(dirStart + dirLength)]));
    }

    [TestMethod]
    public void TryFindValue_NonStandardSpacing_StillParses()
    {
        string loose = "  \"appid\"   \t \"440\" \n \"name\"\t\"Half-Life 2\"\n";
        ReadOnlySpan<char> span = loose.AsSpan();

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "appid", 0, out int appIdStart, out int appIdLength));
        Assert.AreEqual("440", span[appIdStart..(appIdStart + appIdLength)].ToString());

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "name", 0, out int nameStart, out int nameLength));
        Assert.AreEqual("Half-Life 2", span[nameStart..(nameStart + nameLength)].ToString());
    }

    [TestMethod]
    public void TryFindValue_BlockValue_SkipsTheBlockForSiblingKeys()
    {
        string vdf = """
            "outer"
            {
                "inner" { "nested" "deep" }
                "after" "value-after"
            }
            """;

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(vdf.AsSpan(), "after", 0, out int start, out int length));
        Assert.AreEqual("value-after", vdf.AsSpan()[start..(start + length)].ToString());

        // A nested key is not a leaf of the outer scan for "nested" at top
        // level (it lives inside a block value the caller skips).
        Assert.IsFalse(SteamVdfTokenizer.TryFindValue(vdf.AsSpan(), "top", 0, out _, out _));
    }

    [TestMethod]
    public void TryFindValue_TruncatedValue_ReturnsFalseWithoutThrowing()
    {
        // The appid value is cleanly terminated; the name value is cut mid-span
        // (an unterminated quote), so it yields no value and no exception.
        string truncated = "\"appid\" \"730\"\n\"name\" \"Half";

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(truncated.AsSpan(), "appid", 0, out int start, out int length));
        Assert.AreEqual("730", truncated.AsSpan()[start..(start + length)].ToString());

        Assert.IsFalse(SteamVdfTokenizer.TryFindValue(truncated.AsSpan(), "name", 0, out _, out _));
    }

    [TestMethod]
    public void TryFindValue_TruncatedBlock_ReturnsFalseWithoutThrowing()
    {
        string truncated = "\"libraryfolders\" { \"1\" { \"path\" \"C";

        Assert.IsFalse(SteamVdfTokenizer.TryFindValue(truncated.AsSpan(), "path", 0, out _, out _));
    }

    [TestMethod]
    public void TryFindValue_MissingKey_ReturnsFalse()
    {
        Assert.IsFalse(SteamVdfTokenizer.TryFindValue(AcfWithEscapes.AsSpan(), "no_such_key", 0, out _, out _));
    }

    [TestMethod]
    public void TryFindValue_OrdinalKeyMatch_IsCaseSensitive()
    {
        // VDF keys are case-sensitive: "appid" and "AppID" are distinct.
        string vdf = "\"AppID\" \"5\"";
        Assert.IsFalse(SteamVdfTokenizer.TryFindValue(vdf.AsSpan(), "appid", 0, out _, out _));
        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(vdf.AsSpan(), "AppID", 0, out int start, out int length));
        Assert.AreEqual("5", vdf.AsSpan()[start..(start + length)].ToString());
    }

    [TestMethod]
    public void TryParseUInt32_StateFlagsSpelling_ParsesTheBitmask()
    {
        ReadOnlySpan<char> span = AcfWithEscapes.AsSpan();
        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "StateFlags", 0, out int start, out int length));
        Assert.IsTrue(SteamVdfTokenizer.TryParseUInt32(span[start..(start + length)], out uint flags));
        Assert.AreEqual(4u, flags);
    }

    [TestMethod]
    public void TryParseUInt32_NonNumericValue_ReturnsFalse()
    {
        Assert.IsFalse(SteamVdfTokenizer.TryParseUInt32("not-a-number".AsSpan(), out _));
        Assert.IsFalse(SteamVdfTokenizer.TryParseUInt32(ReadOnlySpan<char>.Empty, out _));
    }

    [TestMethod]
    public void TryParseInt64_ByteCountersAndTimestamps_Parse()
    {
        ReadOnlySpan<char> span = AcfWithEscapes.AsSpan();
        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "BytesDownloaded", 0, out int start, out int length));
        Assert.IsTrue(SteamVdfTokenizer.TryParseInt64(span[start..(start + length)], out long bytes));
        Assert.AreEqual(123456789L, bytes);

        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "LastPlayed", 0, out int timeStart, out int timeLength));
        Assert.IsTrue(SteamVdfTokenizer.TryParseInt64(span[timeStart..(timeStart + timeLength)], out long time));
        Assert.AreEqual(1700000000L, time);
    }

    [TestMethod]
    public void TryParseDouble_HoursOnRecord_ParsesTheInvariantSpelling()
    {
        ReadOnlySpan<char> span = AcfWithEscapes.AsSpan();
        Assert.IsTrue(SteamVdfTokenizer.TryFindValue(span, "hours_on_record", 0, out int start, out int length));
        Assert.IsTrue(SteamVdfTokenizer.TryParseDouble(span[start..(start + length)], out double hours));
        Assert.AreEqual(1234.5, hours, 1e-9);
    }

    [TestMethod]
    public void Unescape_PlainValue_ReturnsTheUnescapedText()
    {
        Assert.AreEqual("plain value", SteamVdfTokenizer.Unescape("plain value".AsSpan()));
        Assert.AreEqual("C:\\Steam", SteamVdfTokenizer.Unescape("C:\\\\Steam".AsSpan()));
        Assert.AreEqual("a\nb\tc", SteamVdfTokenizer.Unescape("a\\nb\\tc".AsSpan()));
    }
}
