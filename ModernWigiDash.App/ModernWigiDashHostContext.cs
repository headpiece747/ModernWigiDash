using System;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Backward-compatible subclass of <see cref="WidgetHostEnvironment"/>.
/// </summary>
internal sealed class ModernWigiDashHostContext : WidgetHostEnvironment
{
    /// <summary>
    /// Constructs a <see cref="ModernWigiDashHostContext"/> delegating to <see cref="WidgetHostEnvironment"/>.
    /// </summary>
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
        : base(
            onUiThread,
            requestRender,
            requestInspectorRefresh,
            showDeviceAuth,
            closeDeviceAuth,
            profileProvider,
            markProfileDirty,
            refreshGlobalHotkeys,
            navigatePage,
            launchAhkScript,
            saveCalendarCredential,
            sensorProvider,
            frameTimeProvider,
            logInfoSink,
            logErrorSink)
    {
    }
}
