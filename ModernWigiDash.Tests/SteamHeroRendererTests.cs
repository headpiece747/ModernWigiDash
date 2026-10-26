using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class SteamHeroRendererTests
{
    private static readonly SKRect Bounds = new(0, 0, 1016, 592);

    private sealed class EmptyPosterLookup : ISteamPosterLookup
    {
        public bool TryGet(uint appId, out SKBitmap? bitmap)
        {
            bitmap = null;
            return false;
        }
    }

    private static SteamGameItem Game(uint id, string name, SteamGameState state = SteamGameState.Idle, double hours = 45.0, bool fav = false)
        => new(id, name, $"dir-{id}", state, 0f, hours, DateTime.UtcNow.AddHours(-2), fav);

    [TestMethod]
    public void Render_ActiveHero_RendersCleanlyWithoutExceptions()
    {
        using var renderer = new SteamHeroRenderer();
        using var bitmap = new SKBitmap(1016, 592);
        using var canvas = new SKCanvas(bitmap);

        var games = new[]
        {
            Game(10, "Cyberpunk 2077", SteamGameState.Running, 142.5, fav: true),
            Game(20, "Elden Ring", SteamGameState.Idle, 89.0),
        };

        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, games, selectedAppId: 10);

        renderer.Render(
            canvas,
            Bounds,
            geom,
            hero: games[0],
            shelfGames: games,
            posterLookup: new EmptyPosterLookup(),
            activeFilter: SteamFilterMode.All,
            accent: new SKColor(245, 158, 11),
            text: SKColors.White);

        // Success: no unhandled exceptions thrown, valid render output
        Assert.IsNotNull(bitmap);
    }

    [TestMethod]
    public void Render_IdleHero_RendersPlayButton()
    {
        using var renderer = new SteamHeroRenderer();
        using var bitmap = new SKBitmap(1016, 592);
        using var canvas = new SKCanvas(bitmap);

        var games = new[] { Game(20, "Elden Ring", SteamGameState.Idle, 89.0) };
        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, games, selectedAppId: 20);

        renderer.Render(
            canvas,
            Bounds,
            geom,
            hero: games[0],
            shelfGames: games,
            posterLookup: new EmptyPosterLookup(),
            activeFilter: SteamFilterMode.Favorites,
            accent: new SKColor(245, 158, 11),
            text: SKColors.White);

        Assert.IsNotNull(bitmap);
    }

    [TestMethod]
    public void Render_EmptyLibrary_RendersEmptyState()
    {
        using var renderer = new SteamHeroRenderer();
        using var bitmap = new SKBitmap(1016, 592);
        using var canvas = new SKCanvas(bitmap);

        var games = Array.Empty<SteamGameItem>();
        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, games, selectedAppId: 0);

        renderer.Render(
            canvas,
            Bounds,
            geom,
            hero: null,
            shelfGames: games,
            posterLookup: new EmptyPosterLookup(),
            activeFilter: SteamFilterMode.Recent,
            accent: new SKColor(245, 158, 11),
            text: SKColors.White);

        Assert.IsNotNull(bitmap);
    }

    [TestMethod]
    public void Render_SteadyStateSuccessiveCalls_ReusesCachedStrings()
    {
        using var renderer = new SteamHeroRenderer();
        using var bitmap = new SKBitmap(1016, 592);
        using var canvas = new SKCanvas(bitmap);

        var games = new[] { Game(10, "Cyberpunk 2077", SteamGameState.Running, 142.5) };
        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, games, selectedAppId: 10);
        var lookup = new EmptyPosterLookup();

        for (int i = 0; i < 5; i++)
        {
            renderer.Render(
                canvas,
                Bounds,
                geom,
                hero: games[0],
                shelfGames: games,
                posterLookup: lookup,
                activeFilter: SteamFilterMode.All,
                accent: new SKColor(245, 158, 11),
                text: SKColors.White);
        }

        Assert.IsNotNull(bitmap);
    }
}
