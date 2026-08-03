# Task 2 Brief: Integrate Fallback Rendering into TwitchChatStreamWidget and Word Wrapping

## Requirements
1. Modify `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`:
   - Update `WrapText` in `SocialAndVisualWidgets.cs` to use `FontHelper.MeasureTextWithFallback(candidate, font)`:
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
   - In `TwitchChatStreamWidget.Render`:
     Replace direct `canvas.DrawText(...)` calls for header badge, status, username, and chat message body lines with `canvas.DrawTextWithFallback(...)`.
   - Add helper method `AddTestChatMessageForTesting(string username, string text)` to `TwitchChatStreamWidget` (under `[TestOnly]` or `internal`) so unit tests can inject test messages with emojis.
2. Modify `ModernWigiDash.Tests/UnitTestSuite.cs`:
   - Add unit test `TwitchWidget_RendersMessagesWithEmojisWithoutErrors`:
     ```csharp
     [Test]
     public void TwitchWidget_RendersMessagesWithEmojisWithoutErrors()
     {
         var widget = new TwitchChatStreamWidget();
         using var bitmap = new SkiaSharp.SKBitmap(400, 300);
         using var canvas = new SkiaSharp.SKCanvas(bitmap);
         var bounds = new SkiaSharp.SKRect(0, 0, 400, 300);
         widget.AddTestChatMessageForTesting("GamerOne", "Hello world! 🔥 🎉 💬");
         Assert.DoesNotThrow(() => widget.Render(canvas, bounds));
     }
     ```
3. Run tests: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj`
4. Commit: `git commit -m "feat(twitch): integrate dynamic font glyph fallback rendering for emojis and symbols"`

## Global Constraints
- Target Framework: `net10.0-windows10.0.19041.0`
- C# standard: `#nullable enable`, zero compiler warnings.
- Write report to: `c:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\.superpowers\sdd\2026-08-03-font-glyph-fallback\task-2-report.md`
