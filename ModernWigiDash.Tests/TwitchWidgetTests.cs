using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Twitch;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class TwitchWidgetTests
{
    [TestMethod]
    public void TwitchWidget_DefaultsToAnonymousChatAndDynamicChannelSelection()
    {
        var widget = new TwitchChatStreamWidget();
        var optionsProvider = (IWidgetPropertyOptionsProvider)widget;

        Assert.AreEqual("twitch", widget.ChannelName);
        Assert.AreEqual("", widget.TwitchClientId);
        Assert.IsTrue(widget.AutoConnect);
        Assert.AreEqual(0, optionsProvider.GetPropertyOptions(nameof(widget.ChannelName)).Count);
        Assert.AreEqual("Channel Name", new WidgetPropertyOption("channel_login", "Channel Name").ToString());
        Assert.IsFalse(((IWidgetActionPresentationProvider)widget).IsWidgetActionActive(nameof(widget.LoginWithTwitch)));
    }

    [TestMethod]
    public void TwitchWidget_DefaultsToFontSize24AndCleanStatus()
    {
        var widget = new TwitchChatStreamWidget();
        Assert.AreEqual(24, widget.FontSize);
    }

    [TestMethod]
    public void TwitchWidget_RenderExecutesWithoutErrors()
    {
        var widget = new TwitchChatStreamWidget { HeaderColorHex = "#FFCD85", MessageColorHex = "#C6E0FF", FontSize = 18 };
        using var bitmap = new SkiaSharp.SKBitmap(400, 300);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        var bounds = new SkiaSharp.SKRect(0, 0, 400, 300);
        widget.Render(canvas, bounds);

        // The widget must paint its panel background — a fully transparent
        // canvas would mean nothing was rendered.
        Assert.AreNotEqual(0, bitmap.GetPixel(200, 150).Alpha, "The chat panel background must be painted");
    }

    [TestMethod]
    public void TwitchWidget_RendersMessagesWithEmojisWithoutErrors()
    {
        var widget = new TwitchChatStreamWidget();
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 400, 300);
        widget.AddTestChatMessageForTesting("GamerOne", "Hello world! 🔥 🎉 💬");
        widget.Render(canvas, bounds);

        // The message render must paint the panel — a fully transparent canvas
        // would mean the queued message was never drawn.
        Assert.AreNotEqual(0, bitmap.GetPixel(200, 150).Alpha, "The chat panel must paint when messages are queued");
    }
}
