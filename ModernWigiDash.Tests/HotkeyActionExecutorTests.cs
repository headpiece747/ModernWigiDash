namespace ModernWigiDash.Tests;

/// <summary>
/// Drives <see cref="HotkeyActionExecutor.ExecuteAsync"/> through a recording
/// <see cref="HotkeyActionApi"/> so the executor's routing policy is assertable
/// without sending real keystrokes or spawning processes: which kind sends which
/// inputs, the repeat/delay clamping, the MaxActions guard, the chord parse, the
/// mouse flags, and the Launch/OpenUrl process starts.
/// </summary>
[TestClass]
public class HotkeyActionExecutorTests
{
    private sealed class RecordingApi
    {
        public List<uint> SendCounts = [];
        public List<(string File, string Args)> ProcessStarts = [];
        // Default: echo the requested count back (the real SendInput reports how
        // many it actually injected). Set to 0 to simulate a rejection.
        public Func<uint, uint> SendBehavior = count => count;

        public HotkeyActionApi Api { get; }

        public RecordingApi()
        {
            var self = this;
            Api = new HotkeyActionApi(
                (uint count, IntPtr buffer, int size, out uint win32Error) => { self.SendCounts.Add(count); win32Error = 0; return self.SendBehavior(count); },
                (file, args) => { self.ProcessStarts.Add((file, args)); });
        }
    }

    [TestCleanup]
    public void ResetApi() => HotkeyActionExecutor.Api = HotkeyActionApi.Default;

    private static Task RunAsync(HotkeyAction action, CancellationToken ct = default)
        => HotkeyActionExecutor.ExecuteAsync([action], ct);

    [TestMethod]
    public async Task KeyChord_SendsDownAndUpBatches()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.KeyChord, Value = "CTRL+A" });

        // A chord of two keys sends two down events then two up events (reversed).
        Assert.AreEqual(2, rec.SendCounts.Count);
        Assert.AreEqual(2u, rec.SendCounts[0]);
        Assert.AreEqual(2u, rec.SendCounts[1]);
    }

    [TestMethod]
    public async Task Text_SendsOneBatchOfTwiceTheCharCount()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.Text, Value = "hi" });

        // Two chars -> four input events (down+up per char) in one batch.
        Assert.AreEqual(1, rec.SendCounts.Count);
        Assert.AreEqual(4u, rec.SendCounts[0]);
    }

    [TestMethod]
    public async Task MouseClick_SendsDownThenUp()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.MouseClick, Value = "Left" });

        Assert.AreEqual(2, rec.SendCounts.Count);
        Assert.AreEqual(1u, rec.SendCounts[0]);
        Assert.AreEqual(1u, rec.SendCounts[1]);
    }

    [TestMethod]
    public async Task MouseDoubleClick_SendsTwoClicks()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.MouseDoubleClick, Value = "Left" });

        // Double click = two clicks = four single-input sends.
        Assert.AreEqual(4, rec.SendCounts.Count);
    }

    [TestMethod]
    public async Task MouseWheel_SendsOneBatch()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.MouseWheel, Value = "Up" });

        Assert.AreEqual(1, rec.SendCounts.Count);
        Assert.AreEqual(1u, rec.SendCounts[0]);
    }

    [TestMethod]
    public async Task Repeat_ClampsToMaxRepeat()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        // Repeat far above the cap clamps to the policy's max; each repeat of a
        // chord sends a down + up batch, so sends == 2 * the clamped repeat.
        int clamped = HotkeyActionPolicy.ClampRepeat(9999);
        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.KeyChord, Value = "A", Repeat = 9999 });

        Assert.AreEqual(2 * clamped, rec.SendCounts.Count);
    }

    [TestMethod]
    public async Task DisabledAction_SendsNothing()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.KeyChord, Value = "A", Enabled = false });

        Assert.AreEqual(0, rec.SendCounts.Count);
    }

    [TestMethod]
    public async Task TooManyActions_Throws()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        var actions = Enumerable.Range(0, HotkeyActionPolicy.MaxActions + 1)
            .Select(i => new HotkeyAction { Kind = HotkeyActionKind.Delay, DelayMs = 1 }).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() => HotkeyActionExecutor.ExecuteAsync(actions, CancellationToken.None));
    }

    [TestMethod]
    public async Task SendInputRejected_Throws()
    {
        // Report zero injected inputs -> the executor treats it as rejected.
        var rec = new RecordingApi { SendBehavior = _ => 0 };
        HotkeyActionExecutor.Api = rec.Api;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new HotkeyAction { Kind = HotkeyActionKind.KeyChord, Value = "A" }));
    }

    [TestMethod]
    public async Task Launch_StartsTheProcessWithArguments()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = "calc.exe", Arguments = "/mode" });

        Assert.AreEqual(1, rec.ProcessStarts.Count);
        Assert.AreEqual("calc.exe", rec.ProcessStarts[0].File);
        Assert.AreEqual("/mode", rec.ProcessStarts[0].Args);
    }

    [TestMethod]
    public async Task OpenUrl_StartsTheProcess()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.OpenUrl, Value = "https://example.com" });

        Assert.AreEqual(1, rec.ProcessStarts.Count);
        Assert.AreEqual("https://example.com", rec.ProcessStarts[0].File);
    }

    [TestMethod]
    public async Task OpenUrl_RejectsDisallowedScheme()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new HotkeyAction { Kind = HotkeyActionKind.OpenUrl, Value = "file:///etc/passwd" }));
        Assert.AreEqual(0, rec.ProcessStarts.Count);
    }

    [TestMethod]
    public async Task MediaKey_RoutesThroughChordSend()
    {
        var rec = new RecordingApi();
        HotkeyActionExecutor.Api = rec.Api;

        await RunAsync(new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "PLAYPAUSE" });

        // A media key is a single-key chord: one down batch + one up batch.
        Assert.AreEqual(2, rec.SendCounts.Count);
        Assert.AreEqual(1u, rec.SendCounts[0]);
    }
}
