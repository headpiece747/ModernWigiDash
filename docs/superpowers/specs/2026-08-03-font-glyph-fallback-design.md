# Dynamic Font Glyph Fallback Design Specification

## Overview
This specification details the dynamic character glyph fallback system for `ModernWigiDash.Core` and `ModernWigiDash.Widgets`. It solves missing character boxes ("tofu" glyphs containing an "X" or square placeholder) when rendering emojis, symbols, or non-Latin characters using `Geist` as the primary application font. The system dynamically queries Windows for missing glyph typefaces via `SKFontManager.Default.MatchCharacter` and renders multi-run text transparently across all widgets.

## Goals & Objectives
1. **Zero Tofu Glyphs**: Eliminate missing glyph boxes when rendering emojis, chat symbols, and unicode characters.
2. **Geist Primary Typography**: Retain `Geist` as the primary font for standard Latin text, numbers, and UI elements.
3. **Dynamic Windows Font Matching**: Query Windows system fonts (`SKFontManager.Default.MatchCharacter`) for any codepoint missing in `Geist` (e.g., `Segoe UI Emoji` or system symbol fonts).
4. **Thread-Safe Caching**: Cache resolved codepoint-to-typeface mappings using `ConcurrentDictionary<int, SKTypeface>` for high performance during rapid chat updates.
5. **.NET 10 & C# Best Practices**: Target `.NET 10.0`, enforce strict nullability (`#nullable enable`), pattern matching, `using` disposal, and SonarAnalyzer compliance.

## Architectural & Component Changes

### 1. `FontHelper` Fallback Extensions
Location: `ModernWigiDash.Core/Rendering/FontHelper.cs`
- Add character matching method:
  ```csharp
  public static SKTypeface GetTypefaceForCodepoint(int codepoint, SKFontStyle style)
  ```
  - First checks if `GeistTypeface.ContainsGlyph(codepoint)` is true. If so, returns `GeistTypeface`.
  - If false, checks `_fallbackCache` dictionary.
  - If not cached, calls `SKFontManager.Default.MatchCharacter(codepoint)` (or fallback `Segoe UI Emoji` / `Segoe UI Symbol`), caches the result, and returns the typeface.

### 2. Multi-Run Fallback Text Drawing & Measurement
Location: `ModernWigiDash.Core/Rendering/FontHelper.cs`
- Add text rendering extension:
  ```csharp
  public static void DrawTextWithFallback(this SKCanvas canvas, string text, float x, float y, SKFont baseFont, SKPaint paint)
  ```
  - Parses input string into Unicode codepoints (handling UTF-32 surrogate pairs).
  - Groups adjacent codepoints that share the same `SKTypeface` into text runs.
  - Renders each run sequentially along the horizontal baseline.
- Add text width measurement extension:
  ```csharp
  public static float MeasureTextWithFallback(string text, SKFont baseFont)
  ```

### 3. Widget Integration (`TwitchChatStreamWidget` & Core Canvas)
Location: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`
- Update `TwitchChatStreamWidget.Render` and `WrapText` to utilize `FontHelper` fallback methods so chat usernames, message body text, and live headers render emojis and unicode symbols cleanly without missing glyph boxes.

## Verification Plan
1. **Automated Unit Tests**:
   - Run unit test suite in `ModernWigiDash.Tests`.
   - Add unit test `FontHelper_GetTypefaceForCodepoint_ReturnsFallbackForEmoji` verifying emoji codepoint (e.g. `0x1F600` 😀) resolves a valid fallback typeface without crashing.
   - Add unit test `FontHelper_MeasureAndDrawWithFallback_ExecutesCleanly` verifying string containing mixed text and emojis processes cleanly.
2. **Manual Verification**:
   - Build `ModernWigiDash.slnx` using `dotnet build`.
   - Run application and verify Twitch chat messages with emojis render cleanly without tofu boxes.
