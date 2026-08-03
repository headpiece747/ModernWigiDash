# Twitch Chat Widget Visual Redesign Design Specification

## Overview
This specification details the visual redesign for `TwitchChatStreamWidget` in `ModernWigiDash.Widgets`. The redesign addresses header clutter/redundancy (duplicate channel names and gibberish titles), improves chat legibility with larger default font sizes and a customizable `FontSize` property, implements block-style username and multi-line word wrapping, and guarantees strict canvas boundary clipping.

## Goals & Objectives
1. **Clean Header Layout**: Remove redundant `#channelname` text and gibberish title strings. Display `#CHANNELNAME` in a top-left badge and live connection status (`● LIVE`, `⟳ Connecting…`, `○ Disconnected`) on the top-right.
2. **Enhanced Typography & Readability**: Increase default chat message font size from `12f` to `16f * scale`, introduce a configurable `FontSize` widget property (range 12–24pt), and scale line heights proportionally.
3. **Block-Style Chat Formatting**: Position usernames on their own line per chat item using the user's Twitch color, allowing chat message body text to wrap edge-to-edge across the full available width.
4. **Strict Boundary Clipping**: Enclose message rendering within Skia `SKCanvas` clip bounds (`canvas.Save()`, `canvas.ClipRect(...)`, `canvas.Restore()`) to ensure no text overflows outside the widget borders or overlaps the header.

## Architectural & Component Changes

### 1. `TwitchChatStreamWidget` Property Updates
Location: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`
- Add new configurable `WidgetProperty`:
  ```csharp
  [WidgetProperty("Font Size", WidgetPropertyType.Number, "Chat text font size in points", 16)]
  public int FontSize { get; set; } = 16;
  ```

### 2. IRC Status Text Sanitization
Location: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`
- Update `_statusDetail` assignment in IRC handlers:
  - Connected state: set `_statusDetail = "LIVE"` (or `""`), eliminating repetitive `#channelname` string concatenation in status text.

### 3. Rendering Pipeline (`Render` Method)
Location: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`

#### A. Header & Subheader Rendering
- **Top Left**: Draw `#` + `NormalizeChannel(ChannelName).ToUpperInvariant()` as a highlighted channel badge at `bounds.Left + pad`.
- **Top Right**: Draw status indicator (`statusText`) aligned to `bounds.Right - pad`.
- **Header Line Clearance**: Set `headerBottom = top + titleSize + 8f * scale`.

#### B. Canvas Boundary Clipping
- Before rendering chat message history, save canvas state and apply clip rect:
  ```csharp
  var contentBounds = new SKRect(bounds.Left + pad, headerBottom, bounds.Right - pad, bounds.Bottom - pad);
  canvas.Save();
  canvas.ClipRect(contentBounds);
  ```
- Restore canvas state with `canvas.Restore()` after message drawing loop completes.

#### C. Block-Style Layout & Word Wrap
- Determine message font size: `float msgSize = Math.Max(10f, FontSize) * scale;`
- Determine line height: `float lineHeight = msgSize * 1.4f;`
- Determine username font size: `float userSize = Math.Max(10f, FontSize - 2f) * scale;`
- For each message snapshot item from bottom to top:
  1. Wrap message body text `m.Text` across `maxTextWidth = contentBounds.Width`.
  2. Compute total block height: `blockH = userLineHeight + (lines.Count * lineHeight) + itemSpacing`.
  3. Adjust vertical cursor: `cursor -= blockH`.
  4. If `cursor < headerBottom`, terminate loop.
  5. Draw Username line at `cursor`: `canvas.DrawText(m.Username, contentBounds.Left, cursor + userLineHeight - 2f * scale, SKTextAlign.Left, userFont, userPaint);`.
  6. Draw wrapped message lines starting at `cursor + userLineHeight`.

## Verification Plan
1. **Automated Unit Tests**:
   - Run existing unit test suite in `ModernWigiDash.Tests` (including `UnitTestSuite.cs` Twitch widget tests).
   - Add unit test verifying default `FontSize` property equals `16`.
   - Add unit test verifying `TwitchChatStreamWidget` rendering logic cleanly executes with updated colors and header formats without crashing.
2. **Manual Verification**:
   - Build `ModernWigiDash.slnx` using `dotnet build`.
   - Verify zero compiler warnings or errors in `ModernWigiDash.Widgets`.
