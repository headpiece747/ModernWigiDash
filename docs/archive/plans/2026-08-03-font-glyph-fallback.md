> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`): `FontHelper.DrawTextWithFallback` / `MeasureTextWithFallback` + Twitch chat integration. Archived for history.

# Dynamic Font Glyph Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement dynamic system font glyph fallback in `FontHelper.cs` using `SKFontManager.Default.MatchCharacter` to eliminate missing character ("box with X" / tofu) glyph artifacts when rendering emojis and unicode symbols in Twitch chat and widget canvas drawing.

**Architecture:** Extend `FontHelper` in `ModernWigiDash.Core` with codepoint-to-typeface matching (`ConcurrentDictionary` cached) and multi-run text drawing/measurement methods (`DrawTextWithFallback`, `MeasureTextWithFallback`), and integrate into `TwitchChatStreamWidget`.

**Tech Stack:** .NET 10.0, SkiaSharp (`SKFontManager`, `SKTypeface`, `SKFont`, `SKCanvas`), MSTest for unit tests.

## Global Constraints

- TargetFramework: `net10.0-windows10.0.19041.0`
- C# standard: `#nullable enable`, zero compiler warnings.

---

### Task 1: Add Codepoint Fallback & Multi-Run Text Utilities to FontHelper.cs

**Files:**
- Modify: `ModernWigiDash.Core/Rendering/FontHelper.cs`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs`

**Interfaces:**
- Produces:
  - `public static SKTypeface GetTypefaceForCodepoint(int codepoint, SKFontStyle style)`
  - `public static void DrawTextWithFallback(this SKCanvas canvas, string text, float x, float y, SKFont baseFont, SKPaint paint, SKTextAlign align = SKTextAlign.Left)`
  - `public static float MeasureTextWithFallback(string text, SKFont baseFont)`

- [ ] **Step 1: Write the failing test**

In `ModernWigiDash.Tests/UnitTestSuite.cs`, add test `FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback`:

```csharp
[Test]
public void FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback()
{
    var geistTf = FontHelper.GeistTypeface;
    var latinTf = FontHelper.GetTypefaceForCodepoint('A', SKFontStyle.Normal);
    Assert.AreEqual(geistTf.FamilyName, latinTf.FamilyName);

    var emojiTf = FontHelper.GetTypefaceForCodepoint(0x1F600, SKFontStyle.Normal); // 😀
    Assert.IsNotNull(emojiTf);
    Assert.AreNotEqual(IntPtr.Zero, emojiTf.Handle);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback"`
Expected: FAIL with "GetTypefaceForCodepoint not found"

- [ ] **Step 3: Implement fallback resolution and multi-run text drawing in FontHelper.cs**

In `ModernWigiDash.Core/Rendering/FontHelper.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace ModernWigiDash.Core.Rendering;

public static class FontHelper
{
    private static readonly ConcurrentDictionary<(int Codepoint, SKFontStyle Weight), SKTypeface> _fallbackCache = new();

    public static SKTypeface GetTypefaceForCodepoint(int codepoint, SKFontStyle style)
    {
        if (GeistTypeface.ContainsGlyph(codepoint)) return GeistTypeface;

        return _fallbackCache.GetOrAdd((codepoint, style), key =>
        {
            var matched = SKFontManager.Default.MatchCharacter(key.Codepoint);
            if (matched != null) return matched;

            return SKTypeface.FromFamilyName("Segoe UI Emoji", key.Weight)
                ?? SKTypeface.FromFamilyName("Segoe UI Symbol", key.Weight)
                ?? SKTypeface.FromFamilyName("Segoe UI", key.Weight)
                ?? SKTypeface.Default;
        });
    }

    public static List<(string Text, SKTypeface Typeface)> GetTextRuns(string text, SKFontStyle style)
    {
        var runs = new List<(string Text, SKTypeface Typeface)>();
        if (string.IsNullOrEmpty(text)) return runs;

        var currentRun = new StringBuilder();
        SKTypeface? currentTf = null;

        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int codepoint = char.ConvertToUtf32(text, i);
            string charStr = char.ConvertFromUtf32(codepoint);
            var tf = GetTypefaceForCodepoint(codepoint, style);

            if (currentTf == null)
            {
                currentTf = tf;
                currentRun.Append(charStr);
            }
            else if (currentTf.Handle == tf.Handle || currentTf.FamilyName == tf.FamilyName)
            {
                currentRun.Append(charStr);
            }
            else
            {
                runs.Add((currentRun.ToString(), currentTf));
                currentRun.Clear();
                currentRun.Append(charStr);
                currentTf = tf;
            }
        }

        if (currentRun.Length > 0 && currentTf != null)
        {
            runs.Add((currentRun.ToString(), currentTf));
        }

        return runs;
    }

    public static float MeasureTextWithFallback(string text, SKFont baseFont)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var runs = GetTextRuns(text, baseFont.Typeface?.GetTypefaceStyle() ?? SKFontStyle.Normal);
        float totalWidth = 0f;

        foreach (var run in runs)
        {
            using var font = new SKFont(run.Typeface, baseFont.Size);
            ConfigureHighQualityFont(font);
            totalWidth += font.MeasureText(run.Text);
        }

        return totalWidth;
    }

    public static void DrawTextWithFallback(this SKCanvas canvas, string text, float x, float y, SKFont baseFont, SKPaint paint, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        var runs = GetTextRuns(text, baseFont.Typeface?.GetTypefaceStyle() ?? SKFontStyle.Normal);

        if (align == SKTextAlign.Right)
        {
            float totalW = MeasureTextWithFallback(text, baseFont);
            x -= totalW;
        }
        else if (align == SKTextAlign.Center)
        {
            float totalW = MeasureTextWithFallback(text, baseFont);
            x -= totalW * 0.5f;
        }

        float currentX = x;
        foreach (var run in runs)
        {
            using var font = new SKFont(run.Typeface, baseFont.Size);
            ConfigureHighQualityFont(font);
            canvas.DrawText(run.Text, currentX, y, SKTextAlign.Left, font, paint);
            currentX += font.MeasureText(run.Text);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Core/Rendering/FontHelper.cs ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(font): add dynamic character fallback and multi-run text utilities to FontHelper"
```

---

### Task 2: Integrate Fallback Rendering into TwitchChatStreamWidget and Word Wrapping

**Files:**
- Modify: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs:645-770`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs`

**Interfaces:**
- Consumes: `FontHelper.DrawTextWithFallback` and `FontHelper.MeasureTextWithFallback`

- [ ] **Step 1: Write failing unit test**

In `ModernWigiDash.Tests/UnitTestSuite.cs`, add test `TwitchWidget_RendersMessagesWithEmojisWithoutErrors`:

```csharp
[Test]
public void TwitchWidget_RendersMessagesWithEmojisWithoutErrors()
{
    var widget = new TwitchChatStreamWidget();
    using var bitmap = new SkiaSharp.SKBitmap(400, 300);
    using var canvas = new SkiaSharp.SKCanvas(bitmap);
    var bounds = new SkiaSharp.SKRect(0, 0, 400, 300);
    
    // Inject chat message containing emojis and unicode symbols
    widget.AddTestChatMessageForTesting("GamerOne", "Hello world! 🔥 🎉 💬");
    Assert.DoesNotThrow(() => widget.Render(canvas, bounds));
}
```

- [ ] **Step 2: Update WrapText and Render methods in TwitchChatStreamWidget**

In `SocialAndVisualWidgets.cs`:
1. Update `WrapText` to use `FontHelper.MeasureTextWithFallback(candidate, font)`:

```csharp
private static List<string> WrapText(string text, SKFont font, float maxWidth)
{
    var result = new List<string>();
    var current = new StringBuilder();
    foreach (var word in text.Split(' '))
    {
        var candidate = current.Length == 0 ? word : current.ToString() + " " + word;
        if (FontHelper.MeasureTextWithFallback(candidate, font) <= maxWidth || current.Length == 0)
        {
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        else
        {
            result.Add(current.ToString());
            current.Clear();
            current.Append(word);
        }
    }
    if (current.Length > 0) result.Add(current.ToString());
    if (result.Count == 0) result.Add("");
    return result;
}
```

2. In `TwitchChatStreamWidget.Render`:
   Replace `canvas.DrawText` calls for username, message lines, and headers with `canvas.DrawTextWithFallback(...)`.

- [ ] **Step 3: Run full test suite to verify tests pass**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj`
Expected: PASS (All tests pass)

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.Widgets/SocialAndVisualWidgets.cs ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(twitch): integrate dynamic font glyph fallback rendering for emojis and symbols"
```
