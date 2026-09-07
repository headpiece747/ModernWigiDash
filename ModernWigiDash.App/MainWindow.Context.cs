using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// MainWindow partial: IModernWigiDashContext host-contract implementation.
/// The dialogs and the inspector panel live in their own modules (DialogHost,
/// Inspector.InspectorController); this file only forwards.
/// <para>
/// The module-deref callbacks (inspector refresh, device authorization,
/// property persistence) are null-tolerant for the startup wiring's
/// pre-module window: before the artifact's HostModules/ProfileLoad steps
/// assign the modules, a callback is a benign no-op instead of the
/// historical startup NRE. A lost RequestInspectorRefresh costs nothing —
/// the artifact's final InitialRefresh step re-establishes the panel after
/// the profile load. A live widget (the callback's source) cannot exist
/// before ProfileLoad, so the no-op loses nothing in practice; the
/// tolerance is the backstop that keeps a future step reorder from being
/// fatal.
/// </para>
/// </summary>
public partial class MainWindow
{
    #region IModernWigiDashContext Implementation for Telemetry & Host Services

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread: synchronously when the
    /// caller is already there, otherwise posted to the dispatcher (fire-and-
    /// forget). The context contract is "safe from any thread", and this is the
    /// one spelling of that hop -- the context facade's marshal-to-dispatcher
    /// sites (page navigation, credential save, inspector refresh) all route
    /// through it instead of each re-spelling the CheckAccess/InvokeAsync pair,
    /// so the hop cannot drift between them.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _ = Dispatcher.InvokeAsync(action);
    }

    // The line policy (flatten + bound + redact) is owned by FileLog.Write; the
    // sink only adds the component tag, so the rule has one enforcement point.
    /// <summary>Widget log sink: the component-tagged INFO line, written
    /// through <see cref="FileLog.Write"/> (the raw value; the flatten/bound/
    /// redact line policy runs at the sink).</summary>
    /// <param name="message">The message text.</param>
    public void LogInfo(string message) => FileLog.Write($"[Display INFO] {message}");

    /// <summary>Widget log sink: the component-tagged ERROR line, appending
    /// the exception when present.</summary>
    /// <param name="message">The message text.</param>
    /// <param name="ex">Optional exception, appended to the line when present.</param>
    public void LogError(string message, Exception? ex = null)
        => FileLog.Write($"[Display ERROR] {message}{(ex is null ? string.Empty : $": {ex}")}");

    /// <summary>Requests a canvas repaint on the dispatcher (a benign no-op
    /// while the canvas is absent).</summary>
    public void RequestRender() => _ = Dispatcher.InvokeAsync(() => SkiaCanvas?.InvalidateVisual());

    /// <summary>Refreshes the inspector panel on the dispatcher; a benign
    /// no-op in the startup wiring's pre-module window (see the type doc).</summary>
    public void RequestInspectorRefresh()
    {
        // Pre-wiring window: a benign no-op (see the type doc) — the
        // artifact's final InitialRefresh step re-establishes the panel.
        Inspector.InspectorController? inspector = _inspector;
        if (inspector is null) return;
        OnUiThread(inspector.Refresh);
    }

    /// <summary>Shows the device-authorization dialog; forwards to the dialog
    /// host (a benign no-op in the pre-module window).</summary>
    /// <param name="serviceName">The service name shown in the dialog header.</param>
    /// <param name="verificationUri">The verification page the dialog opens.</param>
    /// <param name="userCode">The code the user types on the verification page.</param>
    /// <param name="expiresAt">The authorization's expiration time.</param>
    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
        => _dialogHost?.ShowDeviceAuthorization(serviceName, verificationUri, userCode, expiresAt);

    /// <summary>Closes the open device-authorization dialog; forwards to the
    /// dialog host (a benign no-op in the pre-module window).</summary>
    public void CloseDeviceAuthorization()
        => _dialogHost?.CloseDeviceAuthorization();

    /// <summary>
    /// Resolves the placed instance that owns <paramref name="widget"/> (by
    /// identity) and persists the property into its PropertyValues — the
    /// companion write to <see cref="ModernWidgetBase.SetProperty"/> so widget
    /// runtime toggles survive Export→Import. A small linear scan over the
    /// profile; property changes are user-frequency, not per-frame. Pre-wiring
    /// window (a rehydrating widget's init-time write, before ProfileLoad
    /// assigns _profile): a benign no-op — the instance property still carries
    /// the value, only the persistence is skipped.
    /// </summary>
    public void PersistProperty(object widget, string propertyName, object? value)
    {
        // The identity scan is the shared ProfileOps rule (the test context
        // uses the same helper, so the production scan is not a copy).
        if (_profile is { } profile
            && ProfileOps.FindPlacedWidget(profile, widget) is { } placed)
        {
            placed.PropertyValues[propertyName] = value;
            _profilePersistence?.MarkDirty();
        }

        // A global-hotkey chord edit is one of the registration triggers
        // (ADR-0019): re-run the idempotent pass so the OS state follows the
        // edit. The decision lives with the provider (only it knows which of
        // its properties is the chord), so this commit owner stays free of
        // per-widget property names. (A pre-profile window is a benign no-op -
        // the pass guards on the handle, which exists only after Show.)
        if (widget is IGlobalHotkeyProvider { } provider && provider.AffectsGlobalHotkey(propertyName))
            RefreshGlobalHotkeys();
    }

    /// <summary>
    /// Navigates the active page by the delta (the IModernWigiDashContext
    /// seam the hotkey widget's page-flip actions route through): the
    /// window's SwitchToPage seam, whose SetActivePageIndex gate clamps the
    /// page boundary identically to a swipe (an out-of-range step is a
    /// no-op, never a wrap). Marshals to the dispatcher off the UI thread
    /// (the context contract: safe from any thread).
    /// </summary>
    public void NavigatePage(int delta)
    {
        if (delta == 0) return;

        void Navigate()
        {
            if (_profile is { } profile)
                SwitchToPage(profile.ActivePageIndex + delta);
        }
        OnUiThread(Navigate);
    }

    /// <summary>
    /// The AutoHotkey spawn (the IModernWigiDashContext seam the hotkey
    /// widget's Run AHK Script action routes through, ADR-0019): the
    /// interpreter is the user's own, read live from the machine-local
    /// settings at spawn time (a settings write-through is seen on the next
    /// action without a restart). The kill-switch veto, the blank-script
    /// refusal (the widget's fire path skips a blank command before routing,
    /// so this is the seam's own defense in depth), and the interpreter
    /// checks each refuse with one log line (the seam's documented refusal
    /// surface); a spawn is a bare launch, no tracking. Safe from any
    /// thread (Process.Start is thread-safe; the settings read is a
    /// reference read of the record the settings commits swap).
    /// </summary>
    public void LaunchAutoHotkeyScript(string scriptPath)
        => _ahkSpawnPolicy.TryLaunch(scriptPath, _appSettings);

    /// <summary>
    /// Stores the machine-local CalDAV password for a feed through the host's
    /// DPAPI-backed credential store (the IModernWigiDashContext seam the
    /// inspector's feed editor routes through): the secret never rides the
    /// profile, so a traveling profile re-resolves its password on another
    /// machine instead of smuggling it across. Marshals to the dispatcher (the
    /// context contract: safe from any thread); the store's atomic save is the
    /// one write site. A blank feed id or password is a logged no-op (a
    /// half-entered row must not overwrite a good credential with an empty one).
    /// </summary>
    public void SaveCalendarCredential(string feedId, string password)
    {
        if (string.IsNullOrWhiteSpace(feedId) || string.IsNullOrEmpty(password))
        {
            _hotkeyLog.Write("Calendar credential not saved: feed id or password is blank");
            return;
        }

        void Save() => CalendarCredentials.SavePassword(feedId, password);
        OnUiThread(Save);
    }

    #endregion
}


