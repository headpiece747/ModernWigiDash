using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Tests;

/// <summary>
/// TypefaceResolver at its interface: the font fallback ladder's shape and
/// early-out rule, pinned without SkiaSharp native calls or a real font store.
/// The probes are injected delegates (glyph presence per candidate, the
/// matched-typeface lookup), so the decision is assertable where it lives.
/// FontHelper binds the production probes and routes every cache miss through
/// this module; the existing FontAndTextTests still cover the end-to-end
/// resolution through the real typefaces.
/// </summary>
[TestClass]
public class TypefaceResolverTests
{
    private static SKTypeface FakeTypeface(string family) => SKTypeface.FromFamilyName(family) ?? SKTypeface.Default;

    [TestMethod]
    public void ResolveFallback_GeistContainsGlyph_ReturnsGeist()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 'A',
            containsGlyph: (tf, cp) => ReferenceEquals(tf, geist),
            geist: geist,
            emoji: emoji,
            symbol: emoji,
            segoeUi: emoji,
            matchCharacter: _ => null);

        Assert.IsTrue(ReferenceEquals(result, geist));
    }

    [TestMethod]
    public void ResolveFallback_EmojiContainsGlyph_ReturnsEmoji()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 0x1F600,
            containsGlyph: (tf, cp) => ReferenceEquals(tf, emoji) && cp == 0x1F600,
            geist: geist,
            emoji: emoji,
            symbol: emoji,
            segoeUi: emoji,
            matchCharacter: _ => null);

        Assert.IsTrue(ReferenceEquals(result, emoji));
    }

    [TestMethod]
    public void ResolveFallback_SymbolContainsGlyph_ReturnsSymbol()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");
        var symbol = FakeTypeface("Segoe UI Symbol");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 0x2022,
            containsGlyph: (tf, cp) => ReferenceEquals(tf, symbol) && cp == 0x2022,
            geist: geist,
            emoji: emoji,
            symbol: symbol,
            segoeUi: emoji,
            matchCharacter: _ => null);

        Assert.IsTrue(ReferenceEquals(result, symbol));
    }

    [TestMethod]
    public void ResolveFallback_SegoeUiContainsGlyph_ReturnsSegoeUi()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");
        var symbol = FakeTypeface("Segoe UI Symbol");
        var segoeUi = FakeTypeface("Segoe UI");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 'Z',
            containsGlyph: (tf, cp) => ReferenceEquals(tf, segoeUi),
            geist: geist,
            emoji: emoji,
            symbol: symbol,
            segoeUi: segoeUi,
            matchCharacter: _ => null);

        Assert.IsTrue(ReferenceEquals(result, segoeUi));
    }

    [TestMethod]
    public void ResolveFallback_MatchCharacterReturnsTypeface_ReturnsMatched()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");
        var matched = FakeTypeface("SomeOtherFont");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 0x2603,
            containsGlyph: (_, _) => false,
            geist: geist,
            emoji: emoji,
            symbol: emoji,
            segoeUi: emoji,
            matchCharacter: cp => cp == 0x2603 ? matched : null);

        Assert.IsTrue(ReferenceEquals(result, matched));
    }

    [TestMethod]
    public void ResolveFallback_NoCandidateContainsGlyph_ReturnsNull()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 0xFFFF,
            containsGlyph: (_, _) => false,
            geist: geist,
            emoji: emoji,
            symbol: emoji,
            segoeUi: emoji,
            matchCharacter: _ => null);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ResolveFallback_MatchCharacterThrows_LogsAndReturnsNull()
    {
        var geist = FakeTypeface("Geist");
        var emoji = FakeTypeface("Segoe UI Emoji");

        var result = TypefaceResolver.ResolveFallback(
            codepoint: 0x2603,
            containsGlyph: (_, _) => false,
            geist: geist,
            emoji: emoji,
            symbol: emoji,
            segoeUi: emoji,
            matchCharacter: _ => throw new InvalidOperationException("font manager unavailable"));

        Assert.IsNull(result);
    }
}
