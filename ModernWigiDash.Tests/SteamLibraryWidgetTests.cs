using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Steam Library widget's touch routing and persistence: a short tap
/// launches the hit title (the grid cell or the showcase focal card), a 600 ms
/// long-press toggles the favorite under the finger (persisted through the one
/// <c>SetProperty</c> path), and a drag scrolls. The library source, launch
/// sink, and clock are all fakes, so no real Steam install, process, or wall
/// clock is touched; the test drives the service's synchronous <c>RefreshNow</c>
/// seam instead of the 3-second loop.
/// </summary>
[TestClass]
public sealed class SteamLibraryWidgetTests
{
    /// <summary>The design-space bounds every test renders into (the touch geometry's base).</summary>
    private static readonly SKRect Bounds = new(0, 0, SteamGridLayout.DesignWidth, SteamGridLayout.DesignHeight);

    /// <summary>A fake library source: a fixed manifest list, no installation path (no disk warmup).</summary>
    private sealed class FakeSource : ISteamLibrarySource
    {
        private readonly IReadOnlyList<SteamAppManifest> _manifests;

        public FakeSource(params SteamAppManifest[] manifests) => _manifests = manifests;

        public string? SteamPath => null;

        public IReadOnlyList<SteamAppManifest> LoadManifests() => _manifests;
    }

    /// <summary>An installed app manifest (idle, launchable); the ACF's executable drives the process key.</summary>
    private static SteamAppManifest Game(uint appId, string name = "Game", SteamAppState flags = SteamAppState.FullyInstalled)
        => new(appId, name, $"dir-{appId}", "game.exe", flags, 0, 0, 0, 10.0);

    /// <summary>
    /// The shared test rig: a widget over a two-title library (both installed
    /// and launchable), a captured launch sink, and a controllable clock. The
    /// snapshot is produced through the synchronous <c>RefreshNow</c> seam and
    /// the bounds are fixed by one render, so the touch geometry is known.
    /// </summary>
    private static async ValueTask<(SteamLibraryWidget Widget, List<string> Launched, FakeTimeProvider Clock)> RigAsync()
    {
        var source = new FakeSource(Game(100, "Alpha"), Game(200, "Beta"));
        List<string> launched = [];
        var clock = new FakeTimeProvider();
        var widget = new SteamLibraryWidget(source, launched.Add, clock);
        await widget.InitializeAsync(new TestContext()).ConfigureAwait(false);
        widget.ServiceForTest!.RefreshNow();
        using SKBitmap bitmap = new(1, 1);
        widget.Render(new SKCanvas(bitmap), Bounds);
        return (widget, launched, clock);
    }

    /// <summary>The grid cell center for index <paramref name="index"/> at the fixed bounds (scroll 0).</summary>
    private static SKPoint GridCellCenter(int index)
    {
        int columns = SteamGridLayout.ColumnsForWidth(Bounds.Width);
        SteamGridGeometry geometry = SteamGridLayout.Compute(Bounds, columns, 0f);
        SKRect cell = geometry.Cells[index];
        return new SKPoint((cell.Left + cell.Right) / 2f, (cell.Top + cell.Bottom) / 2f);
    }

    /// <summary>The showcase focal-card center at the fixed bounds (focus is the first title, so no previous).</summary>
    private static SKPoint ShowcaseFocusCenter()
    {
        SteamShowcaseGeometry geometry = SteamShowcaseLayout.Compute(Bounds, hasPrevious: false, hasNext: true);
        SKRect card = geometry.FocusCard;
        return new SKPoint((card.Left + card.Right) / 2f, (card.Top + card.Bottom) / 2f);
    }

    /// <summary>Delivers a touch pair (down at t0, up after <paramref name="press"/>) at <paramref name="point"/>.</summary>
    private static void Touch(SteamLibraryWidget widget, SKPoint point, FakeTimeProvider clock, TimeSpan press)
    {
        widget.OnTouch(point, TouchEventType.TouchDown);
        clock.Advance(press);
        widget.OnTouch(point, TouchEventType.TouchUp);
    }

    [TestMethod]
    public async ValueTask GridTap_LaunchesTheHitGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(0), clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask GridTap_SecondCell_LaunchesTheSecondGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(1), clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/200" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask ShowcaseTap_LaunchesTheFocusedGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();
        widget.LayoutMode = "Showcase";

        Touch(widget, ShowcaseFocusCenter(), clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask LongPress_TogglesTheFavoriteUnderTheFinger()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(0), clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        // A long press favorites, it never launches.
        Assert.IsEmpty(launched);
        Assert.AreEqual("[100]", widget.ServiceForTest!.FavoritesJson);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask ShortTap_DoesNotFavorite()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(0), clock, TimeSpan.FromMilliseconds(100));

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        Assert.AreEqual("[]", widget.ServiceForTest!.FavoritesJson);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask LongPress_TogglingOff_RemovesTheFavorite()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();
        widget.FavoritesJson = "[100]";

        Touch(widget, GridCellCenter(0), clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        Assert.IsEmpty(launched);
        Assert.AreEqual("[]", widget.ServiceForTest!.FavoritesJson);
        await widget.DisposeAsync();
    }
}
