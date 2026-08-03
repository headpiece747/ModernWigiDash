# Task 1 Brief: Add Codepoint Fallback & Multi-Run Text Utilities to FontHelper.cs

## Requirements
1. Modify `ModernWigiDash.Core/Rendering/FontHelper.cs`:
   - Implement:
     ```csharp
     public static SKTypeface GetTypefaceForCodepoint(int codepoint, SKFontStyle style)
     public static List<(string Text, SKTypeface Typeface)> GetTextRuns(string text, SKFontStyle style)
     public static float MeasureTextWithFallback(string text, SKFont baseFont)
     public static void DrawTextWithFallback(this SKCanvas canvas, string text, float x, float y, SKFont baseFont, SKPaint paint, SKTextAlign align = SKTextAlign.Left)
     ```
   - Use `ConcurrentDictionary<(int Codepoint, SKFontStyle Style), SKTypeface>` cache for `GetTypefaceForCodepoint`.
   - Call `SKFontManager.Default.MatchCharacter(codepoint)` for missing glyphs in `GeistTypeface`, falling back to `Segoe UI Emoji` / `Segoe UI Symbol` / `Segoe UI` / `SKTypeface.Default`.
2. Modify `ModernWigiDash.Tests/UnitTestSuite.cs`:
   - Add unit test `FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback`:
     ```csharp
     [Test]
     public void FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback()
     {
         var geistTf = FontHelper.GeistTypeface;
         var latinTf = FontHelper.GetTypefaceForCodepoint('A', SKFontStyle.Normal);
         Assert.AreEqual(geistTf.FamilyName, latinTf.FamilyName);

         var emojiTf = FontHelper.GetTypefaceForCodepoint(0x1F600, SKFontStyle.Normal);
         Assert.IsNotNull(emojiTf);
         Assert.AreNotEqual(IntPtr.Zero, emojiTf.Handle);
     }
     ```
3. Run tests: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback"`
4. Commit: `git commit -m "feat(font): add dynamic character fallback and multi-run text utilities to FontHelper"`

## Global Constraints
- Target Framework: `net10.0-windows10.0.19041.0`
- C# standard: `#nullable enable`, zero compiler warnings.
- Write report to: `c:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\.superpowers\sdd\2026-08-03-font-glyph-fallback\task-1-report.md`
