// <copyright file="IDisplayTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>


namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Defines the transport contract for streaming raw frame buffers and issuing
/// hardware control commands to the USB display device. The underlying USB I/O
/// (WinUSB/LibUsbDotNet control transfers and bulk writes) is synchronous and
/// blocking, so the contract is synchronous: async wrappers over blocking I/O
/// would be fake async and force sync-over-async bridges at the callers.
/// Executed entirely in user mode without requiring Administrator/UAC elevation or background services.
/// </summary>
public interface IDisplayTransport : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the hardware transport connection is currently active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the resolved device interface path (WinUSB) or a transport label
    /// (LibUsbDotNet), or "Disconnected" when no device is connected.
    /// </summary>
    string DevicePath { get; }

    /// <summary>
    /// Connects to the USB display hardware via WinUSB setup API.
    /// </summary>
    /// <returns>True if connection and hardware initialization succeeded; otherwise, false.</returns>
    bool Connect();

    /// <summary>
    /// Disconnects from the hardware device and releases all WinUSB pipe handles.
    /// </summary>
    void Disconnect();

    /// <summary>
    /// Sets the physical display brightness percentage (0 to 100%).
    /// </summary>
    /// <param name="brightnessPercent">Brightness level between 0 and 100.</param>
    /// <returns>True if the vendor command succeeded; otherwise, false.</returns>
    bool SetBrightness(byte brightnessPercent);

    /// <summary>
    /// Streams a raw frame buffer payload to the hardware display.
    /// </summary>
    /// <param name="frameBuffer">Raw RGB565 Little Endian pixel payload.</param>
    /// <returns>True if frame framing and bulk transfer succeeded; otherwise, false.</returns>
    bool SendFrame(ReadOnlyMemory<byte> frameBuffer);

    /// <summary>
    /// Switches the display to the specified screen.
    /// </summary>
    /// <param name="screenId">Screen ID: 0x01=Welcome, 0x20=Base0, 0x21=Base1, 0x22=Base2.</param>
    /// <param name="transition">Transition effect (0=none).</param>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    bool GoToScreen(byte screenId, byte transition = 0);

    /// <summary>
    /// Clears the screen configuration for the specified page.
    /// </summary>
    /// <param name="page">Page number to clear (0-2).</param>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    bool ClearPage(byte page = 0);

    /// <summary>
    /// Clears the display timeout/heartbeat.
    /// </summary>
    /// <returns>True if the command succeeded; otherwise, false.</returns>
    bool ClearTimeout();

    /// <summary>
    /// Reads the latest touch report from the display, or null when none is pending.
    /// </summary>
    TouchReport? ReadTouch();

    /// <summary>
    /// Sends the device initialization sequence (PING + blank frame + GoToScreen).
    /// </summary>
    bool SendInitCommands();

    /// <summary>
    /// Puts the display into standby: switches to the built-in vendor Welcome
    /// screen. Heartbeats (<see cref="ClearTimeout"/>) must NOT be sent
    /// afterwards — the display sleeps on its own timeout once the heartbeat
    /// source stops. Returns false when not connected.
    /// </summary>
    bool GoToStandby();
}
