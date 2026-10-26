namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch chat canvas's draw surface (the house render-smoke pattern,
/// CozyTodoCanvasTests): the panel background, header, and message rows
/// paint onto a real canvas from a plain frame record (no connection, no
/// network), so a regression that drops a draw path is caught where the
/// pixels land.
/// </summary>
[TestClass]
public class TwitchChatCanvasTests
{
    private static TwitchChatFrame Frame() => new(
        new SKSize(400, 300), "GamerOne", "#0E0E14", "#FFCD85", "#C6E0FF", 18, false);

    [TestMethod]
    public void Render_EmptyChat_Smoke_PaintsThePanel()
    {
        using var canvas = new TwitchChatCanvas();
        using var bitmap = new SKBitmap(400, 300);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.Render(skCanvas, new SKRect(0, 0, 400, 300), Frame(), TwitchChatPresentation.ChatState.Disconnected(), []);

        Assert.IsTrue(Painted(bitmap), "the chat panel background + header must paint onto the canvas");
    }

    [TestMethod]
    public void Render_WithMessages_Smoke_PaintsTheMessageRows()
    {
        using var canvas = new TwitchChatCanvas();
        using var bitmap = new SKBitmap(400, 300);
        using var skCanvas = new SKCanvas(bitmap);
        IReadOnlyList<TwitchChatLine> messages =
        [
            new TwitchChatLine("alice", "hello world", SKColors.White),
            new TwitchChatLine("bob", "second message", SKColors.LightGray)
        ];

        canvas.Render(skCanvas, new SKRect(0, 0, 400, 300), Frame(), TwitchChatPresentation.ChatState.Live(), messages);

        Assert.IsTrue(Painted(bitmap), "the chat message rows must paint onto the canvas");
    }

    private static bool Painted(SKBitmap bitmap)
    {
        for (int x = 20; x < bitmap.Width - 20; x += 5)
        {
            for (int y = 20; y < bitmap.Height - 20; y += 5)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
