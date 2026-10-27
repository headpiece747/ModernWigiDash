using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Deep weather runtime engine (Candidate 4): encapsulates Open-Meteo REST fetching,
/// geocoding resolution and disambiguation, atomic display state synthesis, disk cache persistence,
/// and the background 15-minute polling cadence. Eliminates the inverted <see cref="IWeatherFetchHost"/>
/// seam from <see cref="WeatherForecastWidget"/>, transforming the widget into a pure presentation layer.
/// </summary>
internal sealed class WeatherRuntimeEngine : IWeatherFetchHost, IDisposable
{
    private readonly WeatherClient _client;
    private readonly WeatherDisplayState _displayState;
    private readonly WeatherFetchFlow _flow;
    private readonly Func<WeatherLocation> _locationProvider;
    private readonly Func<bool> _isStaticSnapshotProvider;
    private readonly Action? _onRepaint;
    private readonly Action? _onInspectorRefresh;
    private readonly Action<string>? _onWriteback;
    private readonly Action<string>? _logInfo;

    private PollLoop? _refreshPoll;
    private CancellationTokenSource? _pollCts;
    private bool _locationWritebackSuppressed;

    /// <param name="cacheDir">Directory storing weather cache files.</param>
    /// <param name="instanceIdProvider">Yields the placed widget instance ID for isolated cache file naming.</param>
    /// <param name="locationProvider">Yields current location query and coordinates.</param>
    /// <param name="isStaticSnapshotProvider">Yields whether data refreshing is frozen.</param>
    /// <param name="onRepaint">Repaint callback.</param>
    /// <param name="onInspectorRefresh">Inspector rebuild callback (e.g. candidate options updated).</param>
    /// <param name="onWriteback">Location property label writeback callback.</param>
    /// <param name="logError">Error log sink.</param>
    /// <param name="logInfo">Info log sink.</param>
    /// <param name="timeProvider">Optional time provider for testing.</param>
    public WeatherRuntimeEngine(
        string cacheDir,
        Func<string?> instanceIdProvider,
        Func<WeatherLocation> locationProvider,
        Func<bool> isStaticSnapshotProvider,
        Action? onRepaint = null,
        Action? onInspectorRefresh = null,
        Action<string>? onWriteback = null,
        Action<string, Exception?>? logError = null,
        Action<string>? logInfo = null,
        TimeProvider? timeProvider = null)
    {
        _locationProvider = locationProvider;
        _isStaticSnapshotProvider = isStaticSnapshotProvider;
        _onRepaint = onRepaint;
        _onInspectorRefresh = onInspectorRefresh;
        _onWriteback = onWriteback;
        _logInfo = logInfo;

        string safeCacheFallback = Guid.NewGuid().ToString();
        _client = new WeatherClient(
            cacheDir,
            () =>
            {
                string? id = instanceIdProvider();
                string token = ProfileImportSanitizer.IsSafeInstanceId(id) ? id! : safeCacheFallback;
                return $"weather_{token}.json";
            },
            logError: logError);

        if (timeProvider is not null)
        {
            _client.Clock = timeProvider;
        }

        _displayState = new WeatherDisplayState(
            _client,
            WeatherPresentation.UnknownLocationLabel,
            () => _client.Clock.GetUtcNow().UtcDateTime);

        _flow = new WeatherFetchFlow(_client, this);
    }

    /// <summary>
    /// Starts background poll loop, loads cached data, and fires the initial live fetch.
    /// </summary>
    public void Start()
    {
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();

        _ = _flow.RunBootLoadAsync(_pollCts.Token);

        _refreshPoll?.Dispose();
        _refreshPoll = new PollLoop(
            "WEATHER",
            WeatherClient.FetchWindow,
            () => true,
            WeatherRefreshTick,
            () => { },
            msg => _logInfo?.Invoke(msg));
        _refreshPoll.Start();

        _ = FetchLiveWeatherAsync();
    }

    private void WeatherRefreshTick() => RequestRefresh();

    /// <summary>Requests a weather refresh if due under the cadence policy.</summary>
    public void RequestRefresh(bool force = false)
    {
        if (!_flow.CanFetch(force)) return;
        _ = TrackFetchAsync(force);
    }

    private async Task TrackFetchAsync(bool force)
    {
        try
        {
            await _flow.RunFetchAsync(force).ConfigureAwait(false);
        }
        finally
        {
            _onRepaint?.Invoke();
        }
    }

    /// <summary>Suppresses resolved-label writebacks during direct test execution.</summary>
    public void SuppressLocationWriteback() => _locationWritebackSuppressed = true;

    /// <summary>Whether location label writeback is currently suppressed.</summary>
    public bool IsLocationWritebackSuppressed => _locationWritebackSuppressed;

    /// <summary>Invalidates cached identity/weather data and forces a refresh.</summary>
    public void Invalidate(WeatherInvalidationKind kind)
    {
        _client.Invalidate(kind);
        RequestRefresh(force: true);
    }

    /// <summary>Flushes any pending geocoded location label write-back.</summary>
    public void ApplyPendingLocationWriteback()
    {
        if (_client.TakePendingWriteback(_locationProvider(), () => _locationWritebackSuppressed) is not { } pending) return;

        _locationWritebackSuppressed = true;
        try
        {
            _onWriteback?.Invoke(pending);
        }
        finally
        {
            _locationWritebackSuppressed = false;
        }
    }

    /// <summary>Executes city search geocoding.</summary>
    public Task<IReadOnlyList<GeocodeCandidate>> SearchCitiesAsync(string query, CancellationToken ct)
        => _client.SearchCitiesAsync(query, ct);

    /// <summary>Executes live weather fetch through the flow coordinator.</summary>
    public Task<WeatherFetchFlowOutcome> FetchLiveWeatherAsync(bool force = false)
        => _flow.RunFetchAsync(force);

    /// <summary>Loads cached weather data from disk.</summary>
    public Task LoadCachedWeatherAsync(CancellationToken cancellationToken)
        => _flow.RunBootLoadAsync(cancellationToken);

    // ── State & Seam Accessors ──────────────────────────────

    public WeatherDisplayState DisplayState => _displayState;

    public WeatherSnapshotState SnapshotState => _displayState.State;

    public WeatherResolutionState Identity => _displayState.Identity;

    public string ResolvedCityName => _displayState.Identity.ResolvedName;

    public double? CurrentPopulation => _displayState.Identity.Population > 0 ? _displayState.Identity.Population : null;

    public IReadOnlyList<GeocodeCandidate> Candidates => _displayState.Identity.Candidates;

    public string CacheFileName => _client.CacheFileName;

    public int FetchCompletedCount => _client.FetchCompletedCount;

    public bool IsFetchInFlight => _client.IsFetchInFlight;

    public TimeProvider Clock
    {
        get => _client.Clock;
        set => _client.Clock = value;
    }

    public HttpClient? TestHttpClient
    {
        get => _client.TestHttpClient;
        set => _client.TestHttpClient = value;
    }

    public Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride
    {
        get => _client.CacheLoadOverride;
        set => _client.CacheLoadOverride = value;
    }

    internal CancellationTokenSource? PollCts
    {
        get => _pollCts;
        set => _pollCts = value;
    }

    // ── IWeatherFetchHost ───────────────────────────────────

    WeatherLocation IWeatherFetchHost.CurrentLocation => _locationProvider();

    int IWeatherFetchHost.DataVersion => _displayState.DataVersion;

    bool IWeatherFetchHost.IsStaticSnapshot => _isStaticSnapshotProvider();

    CancellationToken IWeatherFetchHost.RunToken => _pollCts?.Token ?? CancellationToken.None;

    void IWeatherFetchHost.QueueLabelWriteback(Func<bool> identityGuard, string value)
        => _client.QueueLabelWriteback(identityGuard, value);

    void IWeatherFetchHost.RequestRender() => _onRepaint?.Invoke();

    void IWeatherFetchHost.RequestInspectorRefresh() => _onInspectorRefresh?.Invoke();

    bool IWeatherFetchHost.TryApply(WeatherApplyRequest request)
        => _displayState.TryApply(request);

    bool IWeatherFetchHost.TryApplyTie(IReadOnlyList<GeocodeCandidate> candidates, Func<bool> identityGuard)
        => _displayState.TryApplyTie(candidates, identityGuard, () => _locationProvider().Location);

    public void Dispose()
    {
        _refreshPoll?.Dispose();
        _refreshPoll = null;
        if (_pollCts is not null)
        {
            try
            {
                _pollCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS was already disposed during cancellation race
            }
            _pollCts.Dispose();
            _pollCts = null;
        }
    }
}
