namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The To-Do companion's refresh owner: the 2-second background poll loop
/// that runs the provider's I/O off the render tick, the immutable-box
/// publish (a new box per refresh, never mutated after publication), the
/// one-line failure log (a failed refresh keeps the last snapshot, never a
/// silent stall), and the provider (re)creation through the
/// <see cref="TodoProviderFactory"/> seam. The render tick reads
/// <see cref="Current"/> only (zero async work, no provider call under a
/// lock); a test drives <see cref="RefreshForTest"/> to prime the published
/// snapshot without waiting for the loop's cadence.
/// </summary>
internal sealed class TodoRefreshOwner : IAsyncDisposable
{
    private readonly TodoProviderFactory _factory;
    private readonly Action<string> _log;
    private readonly Action _requestRender;
    private readonly Lock _gate = new();
    private ITaskProvider? _activeProvider;
    private PollLoop? _refreshLoop;
    private volatile SnapshotBox _published = new(TodoCompanionSnapshot.Empty);

    /// <summary>The refresh owner over the provider factory, the log sink, and the render-request sink.</summary>
    public TodoRefreshOwner(TodoProviderFactory factory, Action<string> log, Action requestRender)
    {
        _factory = factory;
        _log = log;
        _requestRender = requestRender;
    }

    /// <summary>The published snapshot (the render tick's read: the immutable box's value).</summary>
    public TodoCompanionSnapshot Current => _published.Value;

    /// <summary>The active provider (null before the first creation, after disposal).</summary>
    public ITaskProvider? Provider
    {
        get
        {
            lock (_gate) return _activeProvider;
        }
    }

    /// <summary>
    /// Creates the provider for the property values (disposing any previous
    /// one) and starts the refresh loop. Re-entry safe: a rehydrated
    /// instance re-runs this and replaces the previous service in place.
    /// </summary>
    public void Initialize(string providerValue, string tasksJson, string companionName)
    {
        CreateProvider(providerValue, tasksJson, companionName);
        StartRefreshLoop();
    }

    /// <summary>The provider property change: replaces the active provider and fires a refresh.</summary>
    public void SetProvider(string providerValue, string tasksJson, string companionName)
    {
        CreateProvider(providerValue, tasksJson, companionName);
        _ = RefreshAsync();
    }

    /// <summary>Syncs the companion name to the active provider (the local provider honors it) and fires a refresh.</summary>
    public void SetCompanionName(string name)
    {
        ITaskProvider? provider;
        lock (_gate) provider = _activeProvider;
        if (provider is not null)
        {
            provider.SetCompanionName(name);
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// One refresh: the provider's refresh work (the refresh contract's one
    /// call), the immutable-box publish, and the host wake-up. The refresh
    /// does NOT write the profile's task cache (the user's inspector
    /// write-through path); it only publishes the provider's current state,
    /// so a background refresh never clobbers a pending edit (the
    /// one-directional-cache rule). A failure logs one line and keeps the
    /// last published snapshot.
    /// </summary>
    public async Task RefreshAsync()
    {
        ITaskProvider? provider;
        lock (_gate) provider = _activeProvider;
        if (provider is null) return;

        try
        {
            _published = new SnapshotBox(await provider.RefreshAsync().ConfigureAwait(false));
            _requestRender();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"CozyTodo: refresh failed ({ex.Message}); keeping the last snapshot");
        }
    }

    /// <summary>Test seam: drive one refresh synchronously (primes the published snapshot without the loop's cadence).</summary>
    public Task RefreshForTest() => RefreshAsync();

    /// <summary>Disposes the refresh loop and the active provider.</summary>
    public async ValueTask DisposeAsync()
    {
        _refreshLoop?.Dispose();
        _refreshLoop = null;

        ITaskProvider? provider;
        lock (_gate)
        {
            provider = _activeProvider;
            _activeProvider = null;
        }

        if (provider is not null)
            await provider.DisposeAsync().ConfigureAwait(false);
    }

    private void CreateProvider(string providerValue, string tasksJson, string companionName)
    {
        lock (_gate)
        {
            if (_activeProvider is not null)
            {
                _ = _activeProvider.DisposeAsync();
                _activeProvider = null;
            }

            _activeProvider = _factory.Create(providerValue, tasksJson, companionName);
        }
    }

    private void StartRefreshLoop()
    {
        _refreshLoop?.Dispose();
        _refreshLoop = new PollLoop("TODO", TimeSpan.FromSeconds(2), () => true, RefreshTick, () => { }, _log);
        _refreshLoop.Start();
    }

    /// <summary>One poll tick: fire the async refresh (the loop owns the cadence).</summary>
    private void RefreshTick() => _ = RefreshAsync();

    /// <summary>The published-snapshot box: one immutable per-refresh snapshot
    /// published through a volatile reference (the render tick reads the current
    /// box atomically, never a mid-publish struct; a new box per refresh means a
    /// published snapshot is never mutated after publication).</summary>
    private sealed class SnapshotBox(TodoCompanionSnapshot value)
    {
        public TodoCompanionSnapshot Value { get; } = value;
    }
}
