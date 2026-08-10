using ModernWigiDash.App;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// Coverage for the residual untested rules found by the fourth sweep:
/// text truncation/wrapping, plugin-loader branches, the starter-profile
/// unknown-plugin skip, the monitor's sanitizer, and the telemetry cluster's
/// success ticks through its seams.
/// </summary>
[TestClass]
public class ResidualCoverageTests
{
    // ── TextRenderHelper: the most-used shared helper, previously untested ──

    [TestMethod]
    public void TruncateText_ShortText_Unchanged()
    {
        using var surface = SKSurface.Create(new SKImageInfo(100, 50));
        var font = ModernWigiDash.Core.Rendering.FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        string result = ModernWigiDash.Widgets.TextRenderHelper.TruncateText("Hello", font, 200f);

        Assert.AreEqual("Hello", result);
    }

    [TestMethod]
    public void TruncateText_LongText_GetsEllipsis()
    {
        var font = ModernWigiDash.Core.Rendering.FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        string result = ModernWigiDash.Widgets.TextRenderHelper.TruncateText("A very long widget title that cannot fit the space", font, 60f);

        Assert.IsTrue(result.Length < 40, "the result must be shortened");
        StringAssert.EndsWith(result, "…");
    }

    [TestMethod]
    public void WrapText_LongText_SplitsIntoMultipleLines()
    {
        var font = ModernWigiDash.Core.Rendering.FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        var lines = ModernWigiDash.Widgets.TextRenderHelper.WrapText("one two three four five six", font, 50f);

        Assert.IsTrue(lines.Count >= 2, "the text must wrap onto multiple lines");
        Assert.AreEqual("one two three four five six", string.Join(" ", lines));
    }

    // ── WidgetPluginLoader branches ──

    [TestMethod]
    public void WidgetPluginLoader_NonWidgetTypes_Skipped()
    {
        var loader = new WidgetPluginLoader();

        loader.RegisterBuiltInPlugin(typeof(string));

        Assert.AreEqual(0, loader.RegisteredPlugins.Count, "a non-widget type must not register");
    }

    [TestMethod]
    public void WidgetPluginLoader_CreateInstance_UnknownIdReturnsNull()
    {
        var loader = new WidgetPluginLoader();

        Assert.IsNull(loader.CreateInstance("no_such_plugin"));
    }

    // ── StarterProfile: unknown plugins are silently skipped, pages stay ──

    [TestMethod]
    public void StarterProfile_Create_UnknownPluginsSilentlySkipped()
    {
        var profile = new StarterProfile(new WidgetPluginLoader(), new TestContext()).Create();

        Assert.IsNotNull(profile);
        Assert.IsTrue(profile.Pages.Count > 0, "pages still build");
        Assert.IsTrue(profile.Pages.All(p => p.Widgets.Count == 0),
            "unknown plugin ids are dropped, never thrown");
    }

    // ── MediaSessionMonitor.Sanitize ──

    [TestMethod]
    public void MediaSessionMonitor_Sanitize_StripsControlCharsAndCaps()
    {
        Assert.AreEqual("Hello", MediaSessionMonitor.Sanitize("Hello", "fallback"));
        Assert.AreEqual("a b", MediaSessionMonitor.Sanitize("a \u0000b", "fallback"), "control chars go, spaces stay");
        Assert.AreEqual("fallback", MediaSessionMonitor.Sanitize("", "fallback"));
        Assert.AreEqual("fallback", MediaSessionMonitor.Sanitize("\u0001\u0002", "fallback"));
        string longInput = new string('x', 500);
        Assert.AreEqual(256, MediaSessionMonitor.Sanitize(longInput, "fallback").Length, "capped at 256 chars");
    }

    // ── TelemetryProducers: success ticks through the cluster seams ──

    private sealed class AvailableNative : IPresentMonNative
    {
        public bool IsAvailable => true;
        public string? UnavailableReason { get; set; }
        public bool OpenSession() => true;
        public void CloseSession() { }
        public bool TrackProcess(int processId) => true;
        public PresentMonPollResult PollDynamic(int processId)
            => new(new PresentMonDynamicSample(120.0, 100.0, 1.0, 3.0, 119.0, 0, 2.0, 4), PmStatus.Success);
        public IReadOnlyList<double> DrainFrameTimes(int processId) => [];
        public void Dispose() { }
    }

    private sealed class MapSource : ILhmMapSource
    {
        private readonly byte[] _map;
        public MapSource(byte[] map) => _map = map;
        public byte[]? TryReadSensorsMap(out string? error)
        {
            error = null;
            return _map;
        }
    }

    [TestMethod]
    public void TelemetryProducers_FrameTimeTickSuccess_UpdatesStore()
    {
        using var producers = new TelemetryProducers(new AvailableNative(), _ => { }, targetResolver: new TrackedTargetResolver(() => 4321, _ => []));
        FrameTimeStore.Reset();

        producers.FrameTimePollTick();

        var fresh = FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(fresh, "a live sample through the cluster must land in the store");
        Assert.AreEqual(120.0, fresh.Fps, 0.001);
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void TelemetryProducers_SensorTickSuccess_UpdatesStore()
    {
        byte[] map = LhmSharedMemoryReaderTests.BuildSensorsMapFixture();
        using var producers = new TelemetryProducers(new AvailableNative(), _ => { }, lhsReader: new LhmSharedMemoryReader(new MapSource(map)));
        LhmSensorStore.Reset();

        producers.SensorPollTick();

        var snap = LhmSensorStore.ReadSnapshot();
        Assert.IsTrue(snap.IsConnected, "a live sensor map through the cluster must connect the store");
        Assert.IsTrue(snap.Readings.Count > 0);
        LhmSensorStore.Reset();
    }
}
