namespace ModernWigiDash.Widgets.Todo;

using System.Text.Json;

/// <summary>
/// Headless HTTP bridge to the Super Productivity desktop app local REST API (default http://127.0.0.1:3876).
/// Auto-reads the local access token from %APPDATA%\superProductivity\local-rest-api-token.
/// </summary>
public sealed class SuperProductivityTaskProvider : ITaskProvider
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private readonly SuperProductivityClient _client;
    private readonly TimeProvider _clock;
    private readonly TodoStore _store;
    private readonly Lock _gate = new();
    private List<TodoItem> _cachedTasks = [];
    private FocusTimerData _focusTimerData = new();
    private CozyCompanionState _companion;

    /// <summary>
    /// Initializes a new instance of <see cref="SuperProductivityTaskProvider"/>.
    /// </summary>
    public SuperProductivityTaskProvider(
        string baseUrl = "http://127.0.0.1:3876",
        Func<string?>? tokenResolver = null,
        HttpClient? httpClient = null,
        TimeProvider? clock = null,
        TodoStore? store = null)
    {
        _client = new SuperProductivityClient(baseUrl, tokenResolver ?? DefaultTokenResolver, httpClient ?? SharedHttpClient);
        _clock = clock ?? TimeProvider.System;
        _store = store ?? new TodoStore();
        _companion = _store.Read().Companion;
    }

    /// <summary>Default disk token reader from %APPDATA%\superProductivity\local-rest-api-token.</summary>
    public static string? DefaultTokenResolver()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string tokenPath = Path.Combine(appData, "superProductivity", "local-rest-api-token");
            if (File.Exists(tokenPath))
            {
                string token = File.ReadAllText(tokenPath).Trim();
                if (!string.IsNullOrEmpty(token)) return token;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Silently fall through to null
        }
        return null;
    }

    /// <summary>Retrieves current tasks from the server (concrete face; the widget reads tasks through the published snapshot).</summary>
    public async ValueTask<IReadOnlyList<TodoItem>> GetTasksAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TodoItem>? fetched;
        try
        {
            fetched = await _client.GetTasksAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // An unconfirmed read (a network fault, an unparseable body, or
            // a cancellation): the cached view stays.
            fetched = null;
        }

        if (fetched is null)
        {
            lock (_gate) return _cachedTasks;
        }

        lock (_gate)
        {
            _cachedTasks = [.. fetched];
            return _cachedTasks;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<TodoItem?> CompleteTaskAsync(string taskId, bool isDone, CancellationToken ct = default)
    {
        TaskMutationOutcome outcome = await _client.CompleteTaskAsync(taskId, isDone, ct).ConfigureAwait(false);
        if (outcome != TaskMutationOutcome.Confirmed)
        {
            // Rejected (the server declined it) and Faulted (the server
            // never saw it) are both "unconfirmed": the verdict is null and
            // the cache stays untouched — the next poll is the source of
            // truth, and no companion reward fires for an unconfirmed
            // completion.
            return null;
        }

        lock (_gate)
        {
            int idx = _cachedTasks.FindIndex(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return null;
            }

            var existing = _cachedTasks[idx];
            var updated = existing with { IsDone = isDone };
            _cachedTasks[idx] = updated;
            if (isDone != existing.IsDone)
            {
                _companion = CozyCompanionEngine.OnTaskCompleted(_companion, isDone, _clock.GetUtcNow().UtcDateTime);
                PersistCompanion();
            }
            return updated;
        }
    }

    /// <inheritdoc/>
    public ValueTask<FocusState> GetFocusStateAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FocusTimer.Resolve(_focusTimerData, _clock));
        }
    }

    /// <inheritdoc/>
    public ValueTask StartFocusTimerAsync(string? taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _focusTimerData = FocusTimer.Start(_focusTimerData, _clock, taskId);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask StopFocusTimerAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (data, focusXp) = FocusTimer.Stop(_focusTimerData, _clock);
            if (ReferenceEquals(data, _focusTimerData))
            {
                return ValueTask.CompletedTask;
            }

            _focusTimerData = data;
            _companion = CozyCompanionEngine.OnFocusReward(_companion, focusXp);
            PersistCompanion();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Gets the current companion state (concrete face for tests; the widget reads the companion through the published snapshot).</summary>
    internal CozyCompanionState GetCompanionState()
    {
        lock (_gate) return _companion;
    }

    /// <summary>
    /// The companion persistence seam: the one owner of the companion-slice
    /// read-modify-write (the in-memory companion is the value; only the
    /// companion field is rewritten, so a concurrent task or focus write in
    /// the store file is not clobbered). Callers must hold <see cref="_gate"/>.
    /// </summary>
    private void PersistCompanion()
    {
        var state = _store.Read();
        _store.Write(state with { Companion = _companion });
    }

    /// <inheritdoc/>
    public void SetTasks(IReadOnlyList<TodoItem> tasks)
    {
        lock (_gate)
        {
            _cachedTasks = [.. tasks];
        }
    }

    /// <inheritdoc/>
    public void SetCompanionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_gate)
        {
            _companion = _companion with { Name = name.Trim() };
            PersistCompanion();
        }
    }

    /// <inheritdoc/>
    public bool FeedCompanion()
    {
        lock (_gate)
        {
            int treats = _companion.SnackTreats;
            int xp = _companion.TotalXp;
            if (!CozyCompanionEngine.Feed(ref treats, ref xp))
            {
                return false;
            }

            DateTime nowUtc = _clock.GetUtcNow().UtcDateTime;
            _companion = _companion with
            {
                SnackTreats = treats,
                TotalXp = xp,
                LastFedTimeUtc = nowUtc,
            };
            PersistCompanion();
            return true;
        }
    }

    /// <summary>Synchronous, thread-safe snapshot of tasks, companion, and focus state (concrete face; the widget reads the published snapshot).</summary>
    public TodoCompanionSnapshot GetSnapshot(DateTime nowUtc)
    {
        lock (_gate)
        {
            var focus = FocusTimer.Resolve(_focusTimerData, nowUtc);
            // The mood is not in the snapshot: the render tick owns the mood
            // decision (it re-derives it from the state it is drawing).
            return new TodoCompanionSnapshot(_cachedTasks, _companion, focus);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<TodoCompanionSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        // One poll of the server (the unconfirmed-read rule keeps the cached
        // view on a fault); the companion and the focus timer are local
        // state, so no second round-trip.
        await GetTasksAsync(ct).ConfigureAwait(false);
        return GetSnapshot(_clock.GetUtcNow().UtcDateTime);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
