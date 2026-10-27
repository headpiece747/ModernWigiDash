using ModernWigiDash.Widgets;
using System;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Decoupled, stand-alone host services context for widgets: implements <see cref="IModernWigiDashContext"/>
/// along with all optional capability facets (<see cref="IWidgetPropertyPersistingContext"/>,
/// <see cref="IWidgetNavigationContext"/>, <see cref="IWidgetScriptLaunchContext"/>,
/// <see cref="IWidgetCredentialContext"/>, and <see cref="IWidgetTelemetryContext"/>).
/// <para>
/// Decouples widget execution and telemetry reading from WPF <c>Window</c> instances, enabling headless
/// runners, multi-display sessions, and direct unit testing with injected telemetry feeds (ADR-0011 exit plan).
/// </para>
/// </summary>
internal sealed class ModernWigiDashHostContext :
    IModernWigiDashContext,
    IWidgetPropertyPersistingContext,
    IWidgetNavigationContext,
    IWidgetScriptLaunchContext,
    IWidgetCredentialContext,
    IWidgetTelemetryContext
{
    private readonly Action<Action> _onUiThread;
    private readonly Action? _requestRender;
    private readonly Action? _requestInspectorRefresh;
    private readonly Action<string, Uri, string, DateTimeOffset>? _showDeviceAuth;
    private readonly Action? _closeDeviceAuth;
    private readonly Func<ProfileLayout?>? _profileProvider;
    private readonly Action? _markProfileDirty;
    private readonly Action? _refreshGlobalHotkeys;
    private readonly Action<int>? _navigatePage;
    private readonly Action<string>? _launchAhkScript;
    private readonly Action<string, string>? _saveCalendarCredential;
    private readonly Func<SensorSnapshotDto?>? _sensorProvider;
    private readonly Func<FrameTimeSnapshotDto?>? _frameTimeProvider;
    private readonly Action<string>? _logInfoSink;
    private readonly Action<string, Exception?>? _logErrorSink;

    public ModernWigiDashHostContext(
        Action<Action>? onUiThread = null,
        Action? requestRender = null,
        Action? requestInspectorRefresh = null,
        Action<string, Uri, string, DateTimeOffset>? showDeviceAuth = null,
        Action? closeDeviceAuth = null,
        Func<ProfileLayout?>? profileProvider = null,
        Action? markProfileDirty = null,
        Action? refreshGlobalHotkeys = null,
        Action<int>? navigatePage = null,
        Action<string>? launchAhkScript = null,
        Action<string, string>? saveCalendarCredential = null,
        Func<SensorSnapshotDto?>? sensorProvider = null,
        Func<FrameTimeSnapshotDto?>? frameTimeProvider = null,
        Action<string>? logInfoSink = null,
        Action<string, Exception?>? logErrorSink = null)
    {
        _onUiThread = onUiThread ?? (action => action());
        _requestRender = requestRender;
        _requestInspectorRefresh = requestInspectorRefresh;
        _showDeviceAuth = showDeviceAuth;
        _closeDeviceAuth = closeDeviceAuth;
        _profileProvider = profileProvider;
        _markProfileDirty = markProfileDirty;
        _refreshGlobalHotkeys = refreshGlobalHotkeys;
        _navigatePage = navigatePage;
        _launchAhkScript = launchAhkScript;
        _saveCalendarCredential = saveCalendarCredential;
        _sensorProvider = sensorProvider;
        _frameTimeProvider = frameTimeProvider;
        _logInfoSink = logInfoSink;
        _logErrorSink = logErrorSink;
    }

    // ── IModernWigiDashContext ──────────────────────────────

    public void LogInfo(string message)
    {
        if (_logInfoSink is not null)
        {
            _logInfoSink(message);
        }
        else
        {
            FileLog.Write($"[Display INFO] {message}");
        }
    }

    public void LogError(string message, Exception? ex = null)
    {
        if (_logErrorSink is not null)
        {
            _logErrorSink(message, ex);
        }
        else
        {
            FileLog.Write($"[Display ERROR] {message}{(ex is null ? string.Empty : $": {ex}")}");
        }
    }

    public void RequestRender()
    {
        _onUiThread(() => _requestRender?.Invoke());
    }

    public void RequestInspectorRefresh()
    {
        _onUiThread(() => _requestInspectorRefresh?.Invoke());
    }

    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
    {
        _onUiThread(() => _showDeviceAuth?.Invoke(serviceName, verificationUri, userCode, expiresAt));
    }

    public void CloseDeviceAuthorization()
    {
        _onUiThread(() => _closeDeviceAuth?.Invoke());
    }

    // ── IWidgetPropertyPersistingContext ────────────────────

    public void PersistProperty(object widget, string propertyName, object? value)
    {
        if (_profileProvider?.Invoke() is { } profile
            && ProfileOps.FindPlacedWidget(profile, widget) is { } placed)
        {
            placed.PropertyValues[propertyName] = value;
            _markProfileDirty?.Invoke();
        }

        if (widget is IGlobalHotkeyProvider provider && provider.AffectsGlobalHotkey(propertyName))
        {
            _refreshGlobalHotkeys?.Invoke();
        }
    }

    // ── IWidgetNavigationContext ────────────────────────────

    public void NavigatePage(int delta)
    {
        if (delta == 0) return;
        _onUiThread(() => _navigatePage?.Invoke(delta));
    }

    // ── IWidgetScriptLaunchContext ──────────────────────────

    public void LaunchAutoHotkeyScript(string scriptPath)
    {
        _launchAhkScript?.Invoke(scriptPath);
    }

    // ── IWidgetCredentialContext ────────────────────────────

    public void SaveCalendarCredential(string feedId, string password)
    {
        if (string.IsNullOrWhiteSpace(feedId) || string.IsNullOrEmpty(password))
        {
            LogError("Calendar credential not saved: feed id or password is blank");
            return;
        }

        _onUiThread(() => _saveCalendarCredential?.Invoke(feedId, password));
    }

    // ── IWidgetTelemetryContext ─────────────────────────────

    public SensorSnapshotDto? TryReadSensors() => _sensorProvider?.Invoke();

    public FrameTimeSnapshotDto? TryReadFrameTime() => _frameTimeProvider?.Invoke();
}
