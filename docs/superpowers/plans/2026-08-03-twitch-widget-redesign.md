# Twitch Chat Widget Visual Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the Twitch Chat Widget header and chat stream layout in `ModernWigiDash.Widgets`, removing header redundancy, adding a configurable `FontSize` setting with a 16pt default, positioning usernames on their own header line, wrapping chat text across full container width, and enforcing strict Skia canvas clipping.

**Architecture:** Update `TwitchChatStreamWidget` in `SocialAndVisualWidgets.cs` with a new `FontSize` property, clean IRC status detail strings, and refactor `Render()` to draw a modern badge header and clip/wrap chat messages in a block layout.

**Tech Stack:** .NET 10.0, C# 13, SkiaSharp (`SKCanvas`, `SKPaint`, `SKFont`, `SKRect`), NUnit for testing.

## Global Constraints

- TargetFramework: `net10.0-windows10.0.19041.0`
- C# standard: `#nullable enable`, `using` statement disposal for native Skia resources, zero compiler/static analysis warnings.

---

### Task 1: Add FontSize WidgetProperty & Clean Up IRC Status Strings

**Files:**
- Modify: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs:270-305`
- Modify: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs:565-595`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs:240-270`

**Interfaces:**
- Produces: `public int FontSize { get; set; } = 16;` on `TwitchChatStreamWidget`

- [ ] **Step 1: Write the failing test**

Open `ModernWigiDash.Tests/UnitTestSuite.cs` and add test `TwitchWidget_DefaultsToFontSize16AndCleanStatus`:

```csharp
[Test]
public void TwitchWidget_DefaultsToFontSize16AndCleanStatus()
{
    var widget = new TwitchChatStreamWidget();
    Assert.AreEqual(16, widget.FontSize);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~TwitchWidget_DefaultsToFontSize16AndCleanStatus"`
Expected: FAIL with "FontSize property not found on TwitchChatStreamWidget"

- [ ] **Step 3: Implement FontSize property and clean up IRC status strings**

In `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`, add:

```csharp
[WidgetProperty("Font Size", WidgetPropertyType.Number, "Chat text font size in points", 16)]
public int FontSize { get; set; } = 16;
```

And update IRC status detail assignments in `RunIrcLoopAsync`:
```csharp
_statusDetail = "LIVE";
```
(instead of `_statusDetail = "LIVE · #" + NormalizeChannel(ChannelName).ToUpperInvariant();`)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~TwitchWidget_DefaultsToFontSize16AndCleanStatus"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/SocialAndVisualWidgets.cs ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(twitch): add FontSize property and clean up status strings"
```

---

### Task 2: Refactor Render Method with Modern Header, Block Layout, and Skia Canvas Clipping

**Files:**
- Modify: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs:678-774`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs:310-325`

**Interfaces:**
- Consumes: `FontSize` property on `TwitchChatStreamWidget`
- Produces: Visual rendering of header badge `#CHANNELNAME`, status `● LIVE`, block layout with username line and full-width wrapped message text, clipped cleanly within widget boundaries.

- [ ] **Step 1: Write the failing unit test**

In `ModernWigiDash.Tests/UnitTestSuite.cs`, update/add test `TwitchWidget_RenderExecutesWithoutErrors`:

```csharp
[Test]
public void TwitchWidget_RenderExecutesWithoutErrors()
{
    var widget = new TwitchChatStreamWidget { HeaderColorHex = "#FFCD85", MessageColorHex = "#C6E0FF", FontSize = 18 };
    using var bitmap = new SkiaSharp.SKBitmap(400, 300);
    using var canvas = new SkiaSharp.SKCanvas(bitmap);
    var bounds = new SkiaSharp.SKRect(0, 0, 400, 300);
    Assert.DoesNotThrow(() => widget.Render(canvas, bounds));
}
```

- [ ] **Step 2: Run test to verify initial state**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~TwitchWidget_RenderExecutesWithoutErrors"`

- [ ] **Step 3: Implement new Render method**

In `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`, replace `Render` implementation:

```csharp
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / DefaultSize.Width, bounds.Height / DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = SKColor.TryParse(BackgroundHex, out var parsedBg) ? parsedBg : new SKColor(15, 17, 23, 235);
        var headerColor = SKColor.TryParse(HeaderColorHex, out var parsedHeader) ? parsedHeader : SKColors.White;
        var msgColor = SKColor.TryParse(MessageColorHex, out var parsedMsg) ? parsedMsg : new SKColor(248, 250, 252);

        using var bgPaint = new SKPaint { Color = bg, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 14f, 14f, bgPaint);

        float pad = 12f * scale;
        float titleSize = 14f * scale;
        float statusSize = 11f * scale;
        float baseFontSize = Math.Max(10f, FontSize) * scale;
        float msgSize = baseFontSize;
        float userSize = Math.Max(10f, baseFontSize - 2f);
        float lineHeight = msgSize * 1.4f;
        float userLineHeight = userSize * 1.35f;

        using var channelBadgeFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, titleSize);
        using var statusFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, statusSize);
        using var badgePaint = new SKPaint { Color = headerColor, IsAntialias = true };
        using var statusPaint = new SKPaint { Color = headerColor.WithAlpha((byte)(headerColor.Alpha * 0.75f)), IsAntialias = true };

        float top = bounds.Top + pad;
        string channelTag = "#" + NormalizeChannel(ChannelName).ToUpperInvariant();
        canvas.DrawText(channelTag, bounds.Left + pad, top + titleSize, SKTextAlign.Left, channelBadgeFont, badgePaint);

        string statusText = _status switch
        {
            StatusConnected => "● " + (_statusDetail.Length > 0 ? _statusDetail : "LIVE"),
            StatusConnecting => "⟳ Connecting…",
            _ => "○ Disconnected"
        };
        canvas.DrawText(statusText, bounds.Right - pad, top + titleSize, SKTextAlign.Right, statusFont, statusPaint);

        float headerBottom = top + titleSize + 8f * scale;

        ChatMessage[] snapshot;
        lock (_messagesLock) snapshot = _messages.ToArray();

        var contentBounds = new SKRect(bounds.Left + pad, headerBottom, bounds.Right - pad, bounds.Bottom - pad);
        if (contentBounds.Width <= 0 || contentBounds.Height <= 0) return;

        canvas.Save();
        canvas.ClipRect(contentBounds);

        if (snapshot.Length == 0)
        {
            using var emptyFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, msgSize);
            using var emptyPaint = new SKPaint { Color = headerColor.WithAlpha(130), IsAntialias = true };
            var hint = _status switch
            {
                StatusConnected => "Waiting for chat…",
                StatusDisconnected when !AutoConnect => "Tap to connect",
                _ => "Waiting for connection…"
            };
            canvas.DrawText(hint, contentBounds.Left, contentBounds.Top + msgSize, SKTextAlign.Left, emptyFont, emptyPaint);
            canvas.Restore();
            return;
        }

        float cursor = contentBounds.Bottom;

        using var userFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, userSize);
        using var msgFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, msgSize);
        using var userPaint = new SKPaint { IsAntialias = true };
        using var msgPaint = new SKPaint { Color = msgColor, IsAntialias = true };

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            var m = snapshot[i];
            var lines = WrapText(m.Text, msgFont, contentBounds.Width);
            float blockH = userLineHeight + (lines.Count * lineHeight) + (4f * scale);

            cursor -= blockH;
            if (cursor < contentBounds.Top - userLineHeight) break;

            userPaint.Color = m.Color;
            canvas.DrawText(m.Username, contentBounds.Left, cursor + userLineHeight - 2f * scale, SKTextAlign.Left, userFont, userPaint);

            float msgY = cursor + userLineHeight;
            for (int li = 0; li < lines.Count; li++)
            {
                canvas.DrawText(lines[li], contentBounds.Left, msgY + (li + 1) * lineHeight - 3f * scale, SKTextAlign.Left, msgFont, msgPaint);
            }
        }

        canvas.Restore();
    }
```

- [ ] **Step 4: Run all unit tests to verify they pass**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj`
Expected: PASS (All tests pass)

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Widgets/SocialAndVisualWidgets.cs ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(twitch): implement block layout, 16pt default font size, and canvas clipping"
```
