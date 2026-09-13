namespace ModernWigiDash.Widgets.Spotify;

/// <summary>
/// The Spotify now-playing widget: explicit Web-API-backed control (distinct
/// from the passive-SMTC Now Playing widget by data source, not by copy).
/// Reads the active playback state and the user's playlists through the shared
/// <see cref="SpotifySession"/> (device-flow auth reusing the Twitch seams),
/// renders in Spotify green with no logo assets, and degrades to a named
/// "not connected" placeholder when unauthenticated or the API is down.
/// Default placement is full-page (Size5x4) and shrinkable.
/// </summary>
[WidgetMetadata("spotify", "Spotify", Category = "Media & Audio", DefaultGridSize = GridSizePreset.Size5x4)]
public class SpotifyWidget : ModernWidgetBase, IWidgetActionInvoker, IWidgetActionPresentationProvider
{
    private static readonly SKColor SpotifyGreen = new(0x1D, 0xB9, 0x54);

    /// <summary>The "Spotify Client ID": the public Spotify application ID (not a token or secret).</summary>
    [WidgetProperty("Spotify Client ID", WidgetPropertyType.Text, "Public Spotify application ID. This is not a user token or secret.", "")]
    public string SpotifyClientId { get; set; } = "";

    /// <summary>The "Log in with Spotify" action button: authorizes playback access in the browser.</summary>
    [WidgetProperty("Log in with Spotify", WidgetPropertyType.Button, "Authorize playback access in your browser")]
    public string LoginWithSpotify { get; set; } = "";

    /// <summary>The "Log out of Spotify" action button: removes the locally stored Spotify authorization.</summary>
    [WidgetProperty("Log out of Spotify", WidgetPropertyType.Button, "Remove the locally stored Spotify authorization")]
    public string LogoutSpotify { get; set; } = "";

    /// <summary>The "Text Color": the track title / artist text color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Track title and artist text color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Background Color": the widget background color.</summary>
    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Widget background color", "#121212")]
    public string BackgroundHex { get; set; } = "#121212";

    // Hoisted paints: every color is computed per render (theme-driven), so each
    // paint is one field reused via Color mutation — the 30 FPS render allocates
    // no SKPaint.
    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _titlePaint = new() { IsAntialias = true };
    private readonly SKPaint _artistPaint = new() { IsAntialias = true };
    private readonly SKPaint _progressPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    private readonly SemaphoreSlim _authActionGate = new(1, 1);
    private volatile bool _disposed;

    // The latest snapshot, refreshed off-tick by the poll loop; Render reads it
    // without any network call. Null until the first successful read.
    private volatile SpotifyNowPlaying? _nowPlaying;
    private CancellationTokenSource? _pollCts;

    /// <summary>
    /// Test seam for the session. Defaults to the shared singleton; tests bind
    /// an isolated store + fake client so a stored token never performs a real
    /// network call (the FeedFactory image).
    /// </summary>
    internal SpotifySession Session { get; set; } = SpotifySession.Shared;

    /// <summary>
    /// Hands the context to the base and starts the playback poll loop
    /// fire-and-forget. The loop refreshes the snapshot on a 5 s cadence only
    /// while authenticated; when unauthenticated it idles (the placeholder is
    /// drawn from the null snapshot).
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        StartPollLoop();
    }

    /// <summary>Runs the named Spotify action (login, logout) fire-and-forget through the session.</summary>
    public void InvokeWidgetAction(string propertyName)
    {
        if (propertyName is nameof(LoginWithSpotify) or nameof(LogoutSpotify))
            _ = RunSpotifyActionAsync(propertyName);
    }

    /// <summary>The action's display label: "Spotify logged in" for the login action while authenticated.</summary>
    public string? GetWidgetActionLabel(string propertyName)
        => string.Equals(propertyName, nameof(LoginWithSpotify), StringComparison.Ordinal) && Session.IsAuthenticated
            ? "Spotify logged in"
            : null;

    /// <summary>Whether the login action is active (a session is authenticated).</summary>
    public bool IsWidgetActionActive(string propertyName)
        => string.Equals(propertyName, nameof(LoginWithSpotify), StringComparison.Ordinal) && Session.IsAuthenticated;

    private void StartPollLoop()
    {
        if (_pollCts is { IsCancellationRequested: false }) return;
        _pollCts = new CancellationTokenSource();
        CancellationToken token = _pollCts.Token;
        _ = Task.Run(() => PollLoopAsync(token), token);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine("Spotify poll loop canceled during shutdown.");
        }
        catch (Exception ex)
        {
            Context?.LogError("Spotify poll loop failed", ex);
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !Session.IsAuthenticated)
        {
            _nowPlaying = null;
            return;
        }

        string? accessToken = await Session.EnsureAccessTokenAsync(SpotifyClientId, Context, cancellationToken).ConfigureAwait(false);
        if (accessToken == null)
        {
            _nowPlaying = null;
            return;
        }

        var api = CreateApiClient();
        try
        {
            _nowPlaying = await api.GetNowPlayingAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (SpotifyApiException ex) when (ex.IsUnauthorized)
        {
            // A 401 that EnsureAccessTokenAsync did not already refresh: drop the
            // snapshot so the placeholder shows; the next tick retries.
            _nowPlaying = null;
        }
        catch (Exception ex)
        {
            // Transient network/API failure: keep the last good snapshot (or null)
            // and log once; the next tick retries.
            Context?.LogError("Spotify snapshot refresh failed", ex);
        }
    }

    private SpotifyApiClient CreateApiClient()
        => new(SpotifyClientId.Length > 0 ? SpotifyClientId : "test-client-id");

    private async Task RunSpotifyActionAsync(string propertyName)
    {
        // Zero-timeout try-acquire: returns immediately, so there is no wait to cancel.
        if (!await _authActionGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return;

        try
        {
            switch (propertyName)
            {
                case nameof(LoginWithSpotify):
                    await Session.LoginAsync(SpotifyClientId, Context, CancellationToken.None).ConfigureAwait(false);
                    break;
                case nameof(LogoutSpotify):
                    await Session.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
                    _nowPlaying = null;
                    break;
            }

            Context.RequestInspectorRefresh();
            Context.RequestRender();
        }
        catch (Exception ex)
        {
            Context.LogError("Spotify action failed", ex);
        }
        finally
        {
            _authActionGate.Release();
        }
    }

    /// <summary>
    /// Draws the now-playing view (album art placeholder block, title, artist,
    /// progress bar) or the "not connected" placeholder when no snapshot is
    /// available.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var scale = Math.Clamp(Math.Min(bounds.Width / DefaultSize.Width, bounds.Height / DefaultSize.Height), 0.4f, 3f);
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;

        var bg = ColorOf(BackgroundHex, new SKColor(0x12, 0x12, 0x12));
        var text = ColorOf(TextColorHex, SKColors.White);

        _bgPaint.Color = bg;
        canvas.DrawRoundRect(bounds, 16f * scale, 16f * scale, _bgPaint);

        var snapshot = _nowPlaying;
        if (snapshot == null)
        {
            DrawNotConnectedPlaceholder(canvas, bounds, text);
            return;
        }

        float pad = 24f * scale;
        float contentLeft = bounds.Left + pad;
        float contentRight = bounds.Right - pad;
        float contentWidth = contentRight - contentLeft;
        if (contentWidth <= 0) return;

        // Layout: left = album art square, right = title + artist + progress.
        float artSize = Math.Min(contentWidth * 0.4f, (bounds.Bottom - bounds.Top) * 0.7f);
        float artLeft = contentLeft;
        float artTop = bounds.Top + pad;
        var artRect = new SKRect(artLeft, artTop, artLeft + artSize, artTop + artSize);

        // Album art: draw a rounded green block (no logo assets; the image URL is
        // fetched lazily and blitted when available — for now a solid accent block).
        _progressPaint.Color = SpotifyGreen;
        canvas.DrawRoundRect(artRect, 8f * scale, 8f * scale, _progressPaint);

        float textLeft = artLeft + artSize + 24f * scale;
        float textWidth = contentRight - textLeft;
        if (textWidth <= 0) return;

        float titleSize = Math.Max(16f, 34f * scale);
        float artistSize = Math.Max(12f, 20f * scale);
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, titleSize);
        var artistFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, artistSize);

        _titlePaint.Color = text;
        _artistPaint.Color = text.WithAlpha(200);

        float y = artTop + artSize * 0.4f;
        canvas.DrawTextWithFallback(snapshot.TrackName, textLeft, y, titleFont, _titlePaint, SKTextAlign.Left);
        canvas.DrawTextWithFallback(snapshot.ArtistName, textLeft, y + titleSize + 8f * scale, artistFont, _artistPaint, SKTextAlign.Left);

        // Progress bar: a thin track with a green fill proportional to position.
        float barY = artTop + artSize - 16f * scale;
        float barHeight = 4f * scale;
        var trackRect = new SKRect(textLeft, barY, contentRight, barY + barHeight);
        _progressPaint.Color = text.WithAlpha(60);
        canvas.DrawRoundRect(trackRect, barHeight / 2, barHeight / 2, _progressPaint);

        double ratio = snapshot.DurationMs > 0 ? (double)snapshot.PositionMs / snapshot.DurationMs : 0;
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        if (ratio > 0)
        {
            float fillRight = textLeft + (float)(ratio * (contentRight - textLeft));
            var fillRect = new SKRect(textLeft, barY, fillRight, barY + barHeight);
            _progressPaint.Color = SpotifyGreen;
            canvas.DrawRoundRect(fillRect, barHeight / 2, barHeight / 2, _progressPaint);
        }
    }

    private void DrawNotConnectedPlaceholder(SKCanvas canvas, SKRect bounds, SKColor text)
    {
        TextRenderHelper.DrawTitleSubtitlePlaceholder(
            canvas,
            bounds,
            "Spotify not connected",
            "Log in with Spotify to show what's playing",
            text,
            _placeholderTitlePaint,
            _placeholderSubPaint);
    }

    /// <summary>Cancels the poll loop and disposes the hoisted paints.</summary>
    public override ValueTask DisposeAsync()
    {
        _disposed = true;
        // The poll loop swallows its own OperationCanceledException, so Cancel()
        // is sufficient for bounded shutdown (the PriceFeedManager.ShutdownLoops
        // precedent); no await of the task is needed.
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _bgPaint.Dispose();
        _titlePaint.Dispose();
        _artistPaint.Dispose();
        _progressPaint.Dispose();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
        return base.DisposeAsync();
    }
}
