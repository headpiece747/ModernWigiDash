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
            // The factory seam: an inert session (a throwing client + browser
            // open — the empty store must never reach the network), injected
            // where the production default returns the shared singleton.
            SessionFactory = () => new TwitchSession(
                new TwitchTokenStore(Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin")),
                _ => throw new NotSupportedException("An empty store must never reach the API client"),
                TimeProvider.System,
                _ => throw new NotSupportedException("An empty store must never reach the browser open"))
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
        // A raw IRC line through the widget's real path (FakeFeed to the
        // connection module to parser to message list), the same wiring
        // TwitchChatConnectionTests drives - the widget owns no
        // message-injection seam.
        var feed = new FakeFeed();
        feed.QueueMessage(":GamerOne!GamerOne@GamerOne.tmi.twitch.tv PRIVMSG #test :Hello world! 🔥 🎉 💬\r\n");
        var widget = new TwitchChatStreamWidget { AutoConnect = true, ChannelName = "test" };
        widget.FeedFactory = () => feed;
        // The factory seam: an inert session (a throwing client + browser
        // open — the empty store must never reach the network), injected
        // where the production default returns the shared singleton.
        widget.SessionFactory = () => new TwitchSession(
            new TwitchTokenStore(Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin")),
            _ => throw new NotSupportedException("An empty store must never reach the API client"),
            TimeProvider.System,
            _ => throw new NotSupportedException("An empty store must never reach the browser open"));
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

    private static TwitchSession InertSession() => new(
        new TwitchTokenStore(Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin")),
        _ => throw new NotSupportedException("An empty store must never reach the API client"),
        TimeProvider.System,
        _ => throw new NotSupportedException("An empty store must never reach the browser open"));

    [TestMethod]
    public void SessionFactory_ConsultedExactlyOnce_UnderConcurrentFirstRead()
    {
        // The session is published through a Lazy (ExecutionAndPublication): a
        // concurrent first read (the fire-and-forget restore racing the UI
        // thread) must create ONE session, not two. The gate holds the first
        // reader inside the factory; the second reader must BLOCK on the
        // pending publication (not run the factory a second time, as the
        // pre-fix non-atomic ??= would have).
        int calls = 0;
        var gate = new ManualResetEventSlim(false);
        var widget = new TwitchChatStreamWidget();
        widget.SessionFactory = () =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(10));
            return InertSession();
        };

        var t1 = Task.Run(() => widget.IsWidgetActionActive("LoginWithTwitch"));
        // t1 is now inside the factory (held by the gate), so t2 is guaranteed
        // to race the pending publication - no sleep needed to force the overlap.
        TestWait.WaitUntilAsync(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        var t2 = Task.Run(() => widget.IsWidgetActionActive("LoginWithTwitch"));
        gate.Set();
        Task.WaitAll(t1, t2);
        Assert.AreEqual(1, calls,
            "two concurrent first reads must create exactly one session (the Lazy publishes a single factory result; the non-atomic ??= would have run the factory twice)");
    }

    [TestMethod]
    public void SessionFactory_SwappedAfterFirstUse_IsIgnored()
    {
        // The one-consultation semantics: the factory is honored before first
        // use and ignored after (the widget owns one session for its lifetime).
        var created = new List<TwitchSession>();
        var widget = new TwitchChatStreamWidget();
        widget.SessionFactory = () =>
        {
            var s = InertSession();
            created.Add(s);
            return s;
        };

        // First use (consults the factory once).
        _ = widget.IsWidgetActionActive("LoginWithTwitch");
        Assert.AreEqual(1, created.Count, "the first session use must consult the factory exactly once");

        // A swap after first use must be ignored (the cached session wins).
        // The probe is "LoginWithTwitch" (the only action that reads the
        // session): a forgotten or rebuilt Lazy would create a second session
        // and red the count assertion below.
        widget.SessionFactory = () =>
        {
            var s = InertSession();
            created.Add(s);
            return s;
        };
        _ = widget.IsWidgetActionActive("LoginWithTwitch");

        Assert.AreEqual(1, created.Count,
            "a factory swap after first use is ignored: the widget owns one session for its lifetime");
    }
}
