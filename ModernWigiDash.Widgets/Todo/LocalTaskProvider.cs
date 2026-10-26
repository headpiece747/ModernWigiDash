namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// 100% offline local task and companion gamification provider backed by <see cref="TodoStore"/>.
/// </summary>
public sealed class LocalTaskProvider : ITaskProvider
{
    private readonly TodoStore _store;
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private TodoDataState _state;

    /// <summary>
    /// Initializes a new instance of <see cref="LocalTaskProvider"/>.
    /// </summary>
    public LocalTaskProvider(TodoStore? store = null, TimeProvider? clock = null)
    {
        _store = store ?? new TodoStore();
        _clock = clock ?? TimeProvider.System;
        _state = _store.Read();
    }

    /// <summary>Synchronous snapshot of current task count for initialization checks (concrete face; the widget reads tasks through the published snapshot).</summary>
    internal int TaskCount
    {
        get
        {
            lock (_gate) return _state.Tasks.Count;
        }
    }

    /// <summary>Synchronous snapshot of current tasks (concrete face for tests; the widget reads tasks through the published snapshot).</summary>
    internal IReadOnlyList<TodoItem> CurrentTasks
    {
        get
        {
            lock (_gate) return _state.Tasks;
        }
    }

    /// <summary>Gets the current companion state (concrete face for tests; the widget reads the companion through the published snapshot).</summary>
    internal CozyCompanionState GetCompanionState()
    {
        lock (_gate)
        {
            return _state.Companion;
        }
    }

    /// <inheritdoc/>
    public void SetCompanionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_gate)
        {
            _state = _state with
            {
                Companion = _state.Companion with { Name = name.Trim() }
            };
            _store.Write(_state);
        }
    }

    /// <summary>Feeds the companion pet 5 snack treats to earn bonus affection.</summary>
    public bool FeedCompanion()
    {
        lock (_gate)
        {
            var companion = _state.Companion;
            int treats = companion.SnackTreats;
            int xp = companion.TotalXp;

            if (!CozyCompanionEngine.Feed(ref treats, ref xp))
            {
                return false;
            }

            DateTime nowUtc = _clock.GetUtcNow().UtcDateTime;
            _state = _state with
            {
                Companion = companion with
                {
                    SnackTreats = treats,
                    TotalXp = xp,
                    LastFedTimeUtc = nowUtc,
                }
            };
            _store.Write(_state);
            return true;
        }
    }

    /// <summary>Adds a task to the local list (concrete face; the inspector write path is <see cref="SetTasks"/>).</summary>
    internal void AddTask(TodoItem task)
    {
        lock (_gate)
        {
            List<TodoItem> tasks = [.. _state.Tasks, task];
            _state = _state with { Tasks = tasks };
            _store.Write(_state);
        }
    }

    /// <summary>Removes a task from the local list (concrete face for tests).</summary>
    internal bool RemoveTask(string taskId)
    {
        lock (_gate)
        {
            int countBefore = _state.Tasks.Count;
            List<TodoItem> tasks = _state.Tasks.Where(t => !string.Equals(t.Id, taskId, StringComparison.Ordinal)).ToList();
            if (tasks.Count == countBefore)
            {
                return false;
            }

            _state = _state with { Tasks = tasks };
            _store.Write(_state);
            return true;
        }
    }

    /// <inheritdoc/>
    public void SetTasks(IReadOnlyList<TodoItem> tasks)
    {
        lock (_gate)
        {
            _state = _state with { Tasks = tasks.ToList() };
            _store.Write(_state);
        }
    }

    /// <summary>Retrieves current tasks (concrete face; the widget reads tasks through the published snapshot).</summary>
    public ValueTask<IReadOnlyList<TodoItem>> GetTasksAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<TodoItem>>(_state.Tasks.ToList());
        }
    }

    /// <inheritdoc/>
    public ValueTask<TodoItem?> CompleteTaskAsync(string taskId, bool isDone, CancellationToken ct = default)
    {
        lock (_gate)
        {
            int index = _state.Tasks.FindIndex(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
            if (index < 0)
            {
                return ValueTask.FromResult<TodoItem?>(null);
            }

            TodoItem existing = _state.Tasks[index];
            TodoItem updated = existing with { IsDone = isDone };
            List<TodoItem> tasks = [.. _state.Tasks];
            tasks[index] = updated;

            var companion = _state.Companion;
            if (isDone != existing.IsDone)
            {
                companion = CozyCompanionEngine.OnTaskCompleted(companion, isDone, _clock.GetUtcNow().UtcDateTime);
            }

            _state = _state with
            {
                Tasks = tasks,
                Companion = companion,
            };
            _store.Write(_state);
            return ValueTask.FromResult<TodoItem?>(updated);
        }
    }

    /// <inheritdoc/>
    public ValueTask<FocusState> GetFocusStateAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FocusTimer.Resolve(_state.FocusTimer, _clock));
        }
    }

    /// <summary>Synchronous, thread-safe snapshot of tasks, companion, and focus state (concrete face; the widget reads the published snapshot).</summary>
    public TodoCompanionSnapshot GetSnapshot(DateTime nowUtc)
    {
        lock (_gate)
        {
            var focus = FocusTimer.Resolve(_state.FocusTimer, nowUtc);
            // The mood is not in the snapshot: the render tick owns the mood
            // decision (it re-derives it from the state it is drawing).
            return new TodoCompanionSnapshot(_state.Tasks, _state.Companion, focus);
        }
    }

    /// <inheritdoc/>
    public ValueTask<TodoCompanionSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        // Local state is in memory: the refresh is a snapshot of the current
        // state, with no I/O (the file write happens on mutation).
        return ValueTask.FromResult(GetSnapshot(_clock.GetUtcNow().UtcDateTime));
    }

    /// <inheritdoc/>
    public ValueTask StartFocusTimerAsync(string? taskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var timer = FocusTimer.Start(_state.FocusTimer, _clock, taskId);
            if (!ReferenceEquals(timer, _state.FocusTimer))
            {
                _state = _state with { FocusTimer = timer };
                _store.Write(_state);
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask StopFocusTimerAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (timer, focusXp) = FocusTimer.Stop(_state.FocusTimer, _clock);
            if (ReferenceEquals(timer, _state.FocusTimer))
            {
                return ValueTask.CompletedTask;
            }

            _state = _state with
            {
                Companion = CozyCompanionEngine.OnFocusReward(_state.Companion, focusXp),
                FocusTimer = timer,
            };
            _store.Write(_state);
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
