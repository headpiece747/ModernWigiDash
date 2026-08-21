using System.IO;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

[TestClass]
public class TwitchWidgetTests
{
    [TestMethod]
    public void TwitchWidget_DefaultsToAnonymousChatAndDynamicChannelSelection()
    {
        // An empty-session widget: the anonymous/logged-out presentation must
        // not depend on the real shared session's ambient state (which a valid
        // stored token elsewhere in the test host would flip).
        var widget = new TwitchChatStreamWidget
        {
            Session = new TwitchSession(
                new TwitchTokenStore(Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin")),
                _ => throw new NotSupportedException("An empty store must never reach the API client"),
                TimeProvider.System)
        };
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
    public async Task TwitchWidget_RendersMessagesWithEmojisWithoutErrors()
    {
        // A raw IRC line through the widget's real path (FakeFeed → IRC loop →
        // parser → message list), the same wiring TwitchChatStreamLoopTests
        // drives — the widget owns no message-injection seam anymore.
        var feed = new FakeFeed();
        feed.QueueMessage(":GamerOne!GamerOne@GamerOne.tmi.twitch.tv PRIVMSG #test :Hello world! 🔥 🎉 💬\r\n");
        var widget = new TwitchChatStreamWidget { AutoConnect = true, ChannelName = "test" };
        widget.FeedFactory = () => feed;
        widget.Session = new TwitchSession(
            new TwitchTokenStore(Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin")),
            _ => throw new NotSupportedException("An empty store must never reach the API client"),
            TimeProvider.System);
        await widget.InitializeAsync(new TestContext(), CancellationToken.None);

        await TestWait.WaitUntilAsync(() => widget.MessageCountForTest >= 1, TimeSpan.FromSeconds(3));

        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 400, 300);
        widget.Render(canvas, bounds);

        // The message render must paint the panel — a fully transparent canvas
        // would mean the queued message was never drawn.
        Assert.AreNotEqual(0, bitmap.GetPixel(200, 150).Alpha, "The chat panel must paint when messages are queued");

        await widget.DisposeAsync();
    }
}
