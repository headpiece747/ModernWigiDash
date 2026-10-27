namespace ModernWigiDash.Widgets;

using System.Reflection;
using ModernWigiDash.Widgets.Todo;

/// <summary>
/// The Cozy To-Do Companion widget for ModernWigiDash.
/// Combines a gamified virtual pet desktop companion (Style B) with a toggleable Clean Minimalist mode,
/// supporting both a standalone offline local store and local synchronization with Super Productivity.
/// </summary>
[WidgetMetadata("cozy_todo", "To-Do Companion", Category = "Productivity", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class CozyTodoWidget : ModernWidgetBase, IWidgetEditorProvider, IWidgetActionInvoker, IWidgetActionPresentationProvider, ITaskCommitter
{
    /// <summary>Display mode: 'Cozy Companion' or 'Clean Minimalist'.</summary>
    [WidgetProperty("Display Mode", WidgetPropertyType.Choice, "Visual style: 'Cozy Companion' or 'Clean Minimalist'.", "Clean Minimalist", "Cozy Companion", "Clean Minimalist")]
    public string DisplayMode { get; set; } = "Clean Minimalist";

    /// <summary>Task provider: 'Local Store' or 'Super Productivity'.</summary>
    [WidgetProperty("Provider", WidgetPropertyType.Choice, "Task source: 'Local Store' or 'Super Productivity'.", "Local Store", "Local Store", "Super Productivity")]
    public string Provider { get; set; } = "Local Store";

    /// <summary>Primary accent color for XP, highlights, and level curves.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color for XP and highlights.", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>Your companion pet's name.</summary>
    [WidgetProperty("Companion Name", WidgetPropertyType.Text, "Your companion pet's name.", "Pochi")]
    public string CompanionName { get; set; } = "Pochi";

    /// <summary>The "Feed Pet" action button: feed 5 snack treats to earn bonus affection.</summary>
    [WidgetProperty("Feed Pet", WidgetPropertyType.Button, "Feed 5 snack treats to your companion pet")]
    public string FeedPetAction { get; set; } = "";

    /// <summary>Serialized tasks JSON data managed by the inspector or local store.</summary>
    [WidgetProperty("Tasks", WidgetPropertyType.Text, "Serialized tasks JSON.")]
    public string TasksJson { get; set; } = "[]";

    private readonly CozyTodoCanvas _canvas = new();

    // The refresh owner: the 2 s poll loop, the immutable-box publish, the
    // failure log, and the provider (re)creation. Lazy, in the TwitchSession
    // image: bound to the Clock + ProviderFactory values at first use, so a
    // test's injected seams set before InitializeAsync are what the owner
    // runs on.
    private TodoRefreshOwner? _refresh;

    /// <summary>
    /// The task-commit seam: the single commit funnel. Takes the persisted
    /// task JSON string (the rows leg owns the serialization) and owns the
    /// order (provider write, state set, refresh): it parses the string back
    /// to typed items for the provider write, writes the same string to the
    /// <see cref="TasksJson"/> profile cache, and publishes a fresh snapshot.
    /// Replaces the active provider's task collection (Local persists to the
    /// store, the task-state owner; Super Productivity replaces its
    /// in-memory cache, which the next poll reconciles against the server).
    /// <c>TasksJson</c> is a one-directional cache: setting it never
    /// propagates back to a provider, so this is the only path that writes
    /// tasks. The caller (the inspector's persist leg) writes the same string
    /// to the profile.
    /// </summary>
    public void CommitTasks(string tasksJson)
    {
        var tasks = TodoItemCodec.Parse(tasksJson);
        TodoRefreshOwner? refresh = _refresh;
        ITaskProvider? provider = refresh?.Provider;
        if (refresh is null || provider is null) return;
        provider.SetTasks(tasks);
        TasksJson = tasksJson;
        _ = refresh.RefreshAsync();
    }

    /// <summary>Clock seam for test injection (defaults to system time provider). Bound to the refresh owner on first use.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Optional provider factory seam for test injection. Bound to the refresh owner's factory on first use.</summary>
    internal Func<string, ITaskProvider>? ProviderFactory { get; set; }

    /// <inheritdoc/>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

        _canvas.OnTaskToggled = taskId => _ = ToggleTaskAsync(taskId);
        _canvas.OnFeedTapped = () => _ = FeedCompanionAsync();
        _canvas.OnFocusToggled = () => _ = ToggleFocusTimerAsync();

        // A rehydrated instance re-runs this method: replace the previous
        // owner (its loop and provider) instead of orphaning it.
        TodoRefreshOwner? previous = _refresh;
        _refresh = null;
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);

        _refresh = new TodoRefreshOwner(
            new TodoProviderFactory(Clock, ProviderFactory),
            LogDiagnostic,
            () => Context?.RequestRender());
        _refresh.Initialize(Provider, TasksJson, CompanionName);
    }

    /// <summary>Test seam: drive one refresh synchronously through the refresh owner (primes the published snapshot without the loop's cadence).</summary>
    internal Task RefreshForTest()
        => _refresh?.RefreshForTest() ?? Task.CompletedTask;

    /// <summary>Test seam: resolves the tap action for a point synchronously
    /// (the canvas's frame geometry, the gesture's hit test), so a test
    /// drives tap-to-action without the async action leg. A <c>Render</c> must
    /// have primed the canvas geometry first.</summary>
    internal CozyTodoHitAction HandleTapForTest(SKPoint point)
        => _canvas.HandleTapForTest(point);

    private async Task ToggleTaskAsync(string taskId)
    {
        TodoRefreshOwner? refresh = _refresh;
        ITaskProvider? provider = refresh?.Provider;
        if (refresh is null || provider is null) return;

        // The toggle's target read is the published snapshot (the one read
        // site for "the current tasks"), so the toggle and the render tick
        // can no longer disagree by a poll interval.
        var snapshot = refresh.Current;
        var target = snapshot.Tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        if (target is null) return;

        bool newStatus = !target.IsDone;
        await provider.CompleteTaskAsync(taskId, newStatus).ConfigureAwait(false);
        await refresh.RefreshAsync().ConfigureAwait(false);
    }

    private async Task FeedCompanionAsync()
    {
        TodoRefreshOwner? refresh = _refresh;
        ITaskProvider? provider = refresh?.Provider;
        if (refresh is not null && provider is not null && provider.FeedCompanion())
        {
            await refresh.RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task ToggleFocusTimerAsync()
    {
        TodoRefreshOwner? refresh = _refresh;
        ITaskProvider? provider = refresh?.Provider;
        if (refresh is null || provider is null) return;

        var currentFocus = await provider.GetFocusStateAsync().ConfigureAwait(false);
        if (currentFocus.IsRunning)
        {
            await provider.StopFocusTimerAsync().ConfigureAwait(false);
        }
        else
        {
            await provider.StartFocusTimerAsync(null).ConfigureAwait(false);
        }

        await refresh.RefreshAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        base.OnPropertyChanged(propertyName, newValue);

        if (string.Equals(propertyName, nameof(Provider), StringComparison.Ordinal))
        {
            _refresh?.SetProvider(Provider, TasksJson, CompanionName);
        }
        else if (string.Equals(propertyName, nameof(CompanionName), StringComparison.Ordinal))
        {
            _refresh?.SetCompanionName(CompanionName);
        }
        // TasksJson is a one-directional profile cache: a change here never
        // propagates to a provider. The typed CommitTasks seam is the only
        // task-write path, so a profile load or an inspector persist is cache
        // bookkeeping only (the provider owns its own state and reconciles
        // on its next poll).

        Context?.RequestRender();
    }

    /// <inheritdoc/>
    public void InvokeWidgetAction(string propertyName)
    {
        if (string.Equals(propertyName, nameof(FeedPetAction), StringComparison.Ordinal))
        {
            _ = FeedCompanionAsync();
        }
    }

    /// <inheritdoc/>
    public string? GetWidgetActionLabel(string propertyName)
    {
        if (string.Equals(propertyName, nameof(FeedPetAction), StringComparison.Ordinal))
        {
            int treats = _refresh?.Current.Companion.SnackTreats ?? 0;
            return $"Feed {CompanionName} (🍖 {treats} treats)";
        }
        return null;
    }

    /// <inheritdoc/>
    public bool IsWidgetActionActive(string propertyName)
    {
        if (string.Equals(propertyName, nameof(FeedPetAction), StringComparison.Ordinal))
        {
            return CozyCompanionEngine.CanFeed(_refresh?.Current.Companion.SnackTreats ?? 0);
        }
        return false;
    }

    /// <inheritdoc/>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        // Read the published snapshot only: the refresh owner runs the provider's
        // I/O off the render tick, so the tick does zero async work and no provider
        // call under a lock. Before the first InitializeAsync there is no
        // owner, and the empty snapshot is the truthful placeholder state.
        TodoCompanionSnapshot snapshot = _refresh?.Current ?? TodoCompanionSnapshot.Empty;
        DateTime nowUtc = Clock.GetUtcNow().UtcDateTime;
        bool isMinimalist = string.Equals(DisplayMode, "Clean Minimalist", StringComparison.OrdinalIgnoreCase);

        SKColor accent = SkColorCache.ColorOf(AccentColorHex, CozyTodoPalette.AccentAmber);
        long animTick = Clock.GetUtcNow().ToUnixTimeMilliseconds();

        _canvas.Render(canvas, bounds, isMinimalist, snapshot, accent, animTick, nowUtc);
    }

    /// <inheritdoc/>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        _canvas.OnTouch(localPoint, eventType);
        Context?.RequestRender();
    }

    /// <inheritdoc/>
    public EditorKind? GetEditorKind(PropertyInfo property)
    {
        if (string.Equals(property.Name, nameof(TasksJson), StringComparison.Ordinal))
        {
            return EditorKind.TodoManager;
        }
        return null;
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        TodoRefreshOwner? refresh = _refresh;
        _refresh = null;
        if (refresh is not null)
        {
            await refresh.DisposeAsync().ConfigureAwait(false);
        }

        _canvas.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
