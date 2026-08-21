// <copyright file="DisplayProtocolConstants.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>


namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Hardware protocol specifications and constants for the USB display device.
/// Derived from USB protocol analysis and direct WinUSB hardware specifications.
/// </summary>
internal static class DisplayProtocolConstants
{
    /// <summary>
    /// USB Vendor ID for the display device.
    /// </summary>
    public const ushort VendorId = 0x28DA;

    /// <summary>
    /// USB Product ID for the display device.
    /// </summary>
    public const ushort ProductId = 0xEF01;

    /// <summary>
    /// WinUSB-specific device interface GUID registered by the device INF.
    /// </summary>
    public static readonly Guid WinUsbInterfaceGuid = new("{D876A186-7B31-4804-8115-79A87E8941BD}");

    /// <summary>
    /// Active framebuffer width in pixels (1016 = 1024 - 8px border).
    /// 1016x592 widget area within the 1024x600 display.
    /// Aliased from <see cref="DisplayGeometry"/> — the shared single source.
    /// </summary>
    public const int FramebufferWidth = DisplayGeometry.FramebufferWidth;

    /// <summary>
    /// Active framebuffer height in pixels (592 = 600 - 8px border).
    /// Aliased from <see cref="DisplayGeometry"/> — the shared single source.
    /// </summary>
    public const int FramebufferHeight = DisplayGeometry.FramebufferHeight;

    /// <summary>
    /// Bytes per pixel for RGB565 Little Endian format.
    /// Aliased from <see cref="DisplayGeometry"/> — the shared single source.
    /// </summary>
    public const int BytesPerPixel = DisplayGeometry.BytesPerPixel;

    /// <summary>
    /// Total expected frame buffer payload size in bytes (1016 * 592 * 2 = 1,202,944 bytes).
    /// </summary>
    public const int FrameBufferSize = DisplayGeometry.FrameBufferSize;

    /// <summary>
    /// Bulk OUT Endpoint ID for sending frame buffer payloads.
    /// </summary>
    public const byte BulkOutPipeId = 0x01;

    /// <summary>
    /// Vendor OUT Request Type (0x21 = Class | Interface | Host-to-Device).
    /// </summary>
    public const byte VendorOutRequestType = 0x21;

    // Vendor Command Opcodes
    /// <summary>
    /// Vendor Command: Set Brightness (0x51, wValue=0, data=[level byte]).
    /// Verified via vendor decompilation: brightness level is sent in data buffer, not wValue.
    /// </summary>
    public const byte CmdSetBrightness = 0x51;

    /// <summary>
    /// Vendor Command: Clear Screen Config (0x90 = CMD_SCREENCFG_CLEAR).
    /// wValue = page number.
    /// </summary>
    public const byte CmdClearPage = 0x90;

    /// <summary>
    /// Vendor Command: Add Widget to Screen Config (0x91 = CMD_SCREENCFG_WIDGET_ADD).
    /// wValue = (page << 8) | widgetId.
    /// Data = WidgetConfig struct (X:short, Y:short, Width:short, Height:short, BaseClr:ushort, DrawAddr:uint, DrawLock:byte, InvalidateFlag:byte, UpdateFromCache:byte).
    /// </summary>
    public const byte CmdAddWidget = 0x91;

    /// <summary>
    /// Vendor Command: Send UI Command / Go To Screen (0x70 = CMD_SEND_UI_CMD).
    /// wValue = screen command byte: (transition << 2) | screenId.
    /// </summary>
    public const byte CmdGoToScreen = 0x70;

    /// <summary>
    /// Built-in vendor Welcome screen — the display's idle/standby state.
    /// Shown when the host software is not actively driving a Base screen.
    /// </summary>
    public const byte ScreenWelcome = 0x01;

    /// <summary>
    /// Base screen 0 (first app-driven page). screenId range: 0x20..0x22.
    /// </summary>
    public const byte ScreenBase0 = 0x20;

    /// <summary>
    /// Base screen 1 (second app-driven page).
    /// </summary>
    public const byte ScreenBase1 = 0x21;

    /// <summary>
    /// Base screen 2 (third app-driven page).
    /// </summary>
    public const byte ScreenBase2 = 0x22;

    /// <summary>
    /// Vendor Command: Wake device / clear the pending sleep (0x12 = CMD_TIMEOUT_CLEAR).
    /// Verified via vendor Manager decompilation (WigiDashDeviceLegacy.ClearScreenTimeout;
    /// WigiDashDevice.WakeDevice forwards to it — the Manager's own wake ritual).
    /// wValue=0, no data. Sent at the start of the init sequence so a display left
    /// asleep by a previous session's standby is explicitly woken before the
    /// brightness/page/frame work.
    /// </summary>
    public const byte CmdWakeDevice = 0x12;

    /// <summary>
    /// Vendor Command: Put the device to sleep immediately (0x13 = CMD_TIMEOUT_SET).
    /// Verified via vendor Manager decompilation (WigiDashDeviceLegacy.SnoozeDevice —
    /// the Manager's "Put the Device To Sleep" action; its exit ritual is the
    /// Welcome screen followed by this command). wValue=0, no data. The backlight
    /// turns off while the display stays USB-powered and accepts control transfers
    /// while asleep (the touch report's byte 7 carries the sleep state), so a later
    /// connect wakes it through the init sequence. The display has no active
    /// auto-sleep of its own — without this command it idles on the Welcome screen
    /// with the backlight on, so the standby path sends it after the Welcome screen.
    /// </summary>
    public const byte CmdSleepDevice = 0x13;

    /// <summary>
    /// Vendor Command: Widget Framebuffer Write (0x61 = CMD_SDRAM_WIDGET_WRITE).
    /// Sent via control OUT with 8-byte data payload: [offset(4 LE), length(4 LE)].
    /// wValue = (page << 8) | widgetId.
    /// Followed by bulk write of frame data.
    /// </summary>
    public const byte CmdFrameHeader = 0x61;

    /// <summary>
    /// Vendor Command: Widget Framebuffer Write Clear (0x63 = CMD_SDRAM_WIDGET_WRITE_CLEAR).
    /// Sent only when bulk write fails, to abort the pending transfer.
    /// </summary>
    public const byte CmdFrameAbort = 0x63;

    /// <summary>
    /// Control IN request type for class/device queries (device-to-host).
    /// 0xA1 = Class | Interface | Device-to-Host. Used for touch polling.
    /// </summary>
    public const byte ControlInRequestType = 0xA1;

    /// <summary>
    /// Size of the frame header data payload (8 bytes: offset + length).
    /// </summary>
    public const int FrameHeaderDataSize = 8;

    // Touch input constants (derived from BenchLab WigiDash reverse engineering)

    /// <summary>
    /// Vendor Command: Get Touch Input (0x33 = CMD_WIDGET_GET_TOUCH).
    /// Control IN transfer, returns 8-byte touch report.
    /// </summary>
    public const byte CmdGetTouch = 0x33;

    /// <summary>
    /// Size of the touch report payload (8 bytes).
    /// Layout: [Type:1, Reserved:1, X:2(LE signed), Y:2(LE signed), Reserved:2]
    /// Bytes 6-7 carry vendor screen/sleep state; no consumer reads them.
    /// </summary>
    public const int TouchReportSize = 8;

    /// <summary>
    /// Pipe transfer timeouts, ms — the single source the WinUSB backend binds
    /// into the pipe policies and the transport's <c>CloseBound</c> derives
    /// from, so the engine's close invariant ("abandon a close that would
    /// stall behind a hung device") and the timeout that bounds the stall can
    /// never drift apart.
    /// </summary>
    public const int ControlPipeTimeoutMs = 1000;

    /// <summary>Bulk OUT pipe timeout, ms — the dominant contributor to
    /// <c>CloseBound</c>: an in-flight frame write holds the transport lock
    /// for up to this long on a hung device.</summary>
    public const int BulkPipeTimeoutMs = 30000;

    /// <summary>
    /// Touch report: no touch active (Type == 0).
    /// </summary>
    public const byte TouchTypeNone = 0;

    /// <summary>
    /// Touch report: finger down / initial contact (Type == 1).
    /// </summary>
    public const byte TouchTypeDown = 1;

    /// <summary>
    /// Touch report: finger up / release (Type == 2).
    /// Touch action enum: None=0, Down=1, Up=2.
    /// Note: The hardware does NOT have a separate "move" type.
    /// Intermediate touch points during a swipe are sent as Type=1 (Down).
    /// </summary>
    public const byte TouchTypeUp = 2;
}
