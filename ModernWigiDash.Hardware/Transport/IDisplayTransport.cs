// <copyright file="IDisplayTransport.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>


using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The transport contract as the app actually uses it: stream raw frame
/// buffers, read touch reports, and put the display into standby. The
/// protocol-completeness commands (init sequence, page setup, brightness) are
/// implementation details behind the seam — every member here has a production
/// caller, so test fakes stay small and honest.
/// The underlying USB I/O (WinUSB/LibUsbDotNet control transfers and bulk
/// writes) is synchronous and blocking, so the contract is synchronous: async
/// wrappers over blocking I/O would be fake async and force sync-over-async
/// bridges at the callers.
/// </summary>
internal interface IDisplayTransport : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Connects to the USB display hardware (WinUSB first, LibUsbDotNet
    /// fallback) and runs the initialization sequence.
    /// </summary>
    /// <returns>True if connection and hardware initialization succeeded; otherwise, false.</returns>
    bool Connect();

    /// <summary>
    /// Streams a raw frame buffer payload to the hardware display.
    /// </summary>
    /// <param name="frameBuffer">Raw RGB565 Little Endian pixel payload.</param>
    /// <returns>The truthful send outcome — <see cref="Sdk.FrameSendResult.Sent"/> when the
    /// framing and bulk transfer succeeded, <see cref="Sdk.FrameSendResult.Refused"/> when the
    /// transport declined the frame without touching the wire (no connection, or the frame
    /// fails the size contract), or <see cref="Sdk.FrameSendResult.Failed"/> when the transfer
    /// was attempted and failed.</returns>
    Sdk.FrameSendResult SendFrame(ReadOnlyMemory<byte> frameBuffer);

    /// <summary>
    /// Reads the latest touch report from the display, or null when none is pending.
    /// </summary>
    TouchReport? ReadTouch();

    /// <summary>
    /// Puts the display into standby: switches to the built-in vendor Welcome
    /// screen. No heartbeats are sent afterwards — the display sleeps on its
    /// own timeout once the heartbeat source stops. Returns false when not
    /// connected.
    /// </summary>
    bool GoToStandby();
}
