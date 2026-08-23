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

    // The line policy (flatten + bound + redact) is owned by FileLog.Write; the
    // sink only adds the component tag, so the rule has one enforcement point.
    public void LogInfo(string message) => FileLog.Write($"[Display INFO] {message}");
    public void LogError(string message, Exception? ex = null)
        => FileLog.Write($"[Display ERROR] {message}{(ex is null ? string.Empty : $": {ex}")}");
    public void RequestRender() => _ = Dispatcher.InvokeAsync(() => SkiaCanvas?.InvalidateVisual());

    public void RequestInspectorRefresh()
    {
        // Pre-wiring window: a benign no-op (see the type doc) — the
        // artifact's final InitialRefresh step re-establishes the panel.
        Inspector.InspectorController? inspector = _inspector;
        if (inspector is null) return;
        if (Dispatcher.CheckAccess())
        {
            inspector.Refresh();
            return;
        }

        _ = Dispatcher.InvokeAsync(inspector.Refresh);
    }

    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
        => _dialogHost?.ShowDeviceAuthorization(serviceName, verificationUri, userCode, expiresAt);

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
    }

    #endregion
}


