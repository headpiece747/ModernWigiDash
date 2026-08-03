// <copyright file="IDisplayTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>


namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Defines a modular, high-performance C# 13 / .NET 10 interface for streaming
/// raw frame buffers and issuing hardware control commands to the USB display device.
/// Executed entirely in user mode without requiring Administrator/UAC elevation or background services.
/// </summary>
public interface IDisplayTransport : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the hardware transport connection is currently active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the resolved WinUSB device interface path.
    /// </summary>
    string DevicePath { get; }

    /// <summary>
    /// Connects to the USB display hardware asynchronously via WinUSB setup API.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the connection operation.</param>
    /// <returns>True if connection and hardware initialization succeeded; otherwise, false.</returns>
    ValueTask<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the hardware device and releases all WinUSB pipe handles.
    /// </summary>
    ValueTask DisconnectAsync();

    /// <summary>
    /// Sets the physical display brightness percentage (0 to 100%).
    /// </summary>
    /// <param name="brightnessPercent">Brightness level between 0 and 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the vendor command succeeded; otherwise, false.</returns>
    ValueTask<bool> SetBrightnessAsync(byte brightnessPercent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a raw frame buffer payload asynchronously to the hardware display.
    /// </summary>
    /// <param name="frameBuffer">Raw RGB565 Little Endian pixel payload (1,202,944 bytes for 1016x592 active framebuffer).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if frame framing and bulk transfer succeeded; otherwise, false.</returns>
    ValueTask<bool> SendFrameAsync(ReadOnlyMemory<byte> frameBuffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a raw frame buffer payload span directly to the hardware display.
    /// </summary>
    /// <param name="frameBuffer">Raw RGB565 Little Endian pixel payload span.</param>
    /// <returns>True if frame framing and bulk transfer succeeded; otherwise, false.</returns>
    bool SendFrame(ReadOnlySpan<byte> frameBuffer);

    /// <summary>
    /// Switches the display to the specified screen.
    /// </summary>
    /// <param name="screenId">Screen ID: 0x01=Welcome, 0x20=Base0, 0x21=Base1, 0x22=Base2.</param>
    /// <param name="transition">Transition effect (0=none).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    ValueTask<bool> GoToScreenAsync(byte screenId, byte transition = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the screen configuration for the specified page.
    /// </summary>
    /// <param name="page">Page number to clear (0-2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    ValueTask<bool> ClearPageAsync(byte page = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the display timeout/heartbeat.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    ValueTask<bool> ClearTimeoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a widget to the screen configuration.
    /// </summary>
    /// <param name="page">Page number.</param>
    /// <param name="widgetId">Widget ID.</param>
    /// <param name="config">Widget config payload (20 bytes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    ValueTask<bool> AddWidgetAsync(byte page, byte widgetId, byte[] config, CancellationToken cancellationToken = default);
}
