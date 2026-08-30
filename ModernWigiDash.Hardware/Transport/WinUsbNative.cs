// <copyright file="WinUsbNative.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Direct WinUSB P/Invoke for bulk and control transfers.
/// Uses WinUsb_WritePipe synchronously to avoid LibUsbDotNet's problematic
/// WinUsb_WritePipe_Overlapped call.
/// </summary>
internal static class WinUsbNative
{
    private const string WinUsbDll = "winusb.dll";

    // Entry points are spelled explicitly so the binding resolves to the
    // spelled export and a method rename cannot silently change what is
    // called (ADR-0020); PInvokeBindingTests probes each pair against the
    // real DLL at the gate.
    [DllImport(WinUsbDll, EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Initialize(IntPtr DeviceHandle, out IntPtr InterfaceHandle);

    [DllImport(WinUsbDll, EntryPoint = "WinUsb_Free", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Free(IntPtr InterfaceHandle);

    [DllImport(WinUsbDll, EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_SetPipePolicy(
        IntPtr InterfaceHandle, byte PipeId, uint Id, uint Length, IntPtr Value);

    public const uint PipeTransferTimeout = 3;

    /// <summary>
    /// Synchronous bulk write using WinUsb_WritePipe with a pinned IntPtr buffer.
    /// </summary>
    [DllImport(WinUsbDll, EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_WritePipe(
        IntPtr InterfaceHandle,
        byte PipeId,
        IntPtr Buffer,
        uint BufferLength,
        out uint NumberOfBytesTransferred,
        IntPtr Overlapped);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinUsbSetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [DllImport(WinUsbDll, EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ControlTransfer(
        IntPtr InterfaceHandle,
        [In] WinUsbSetupPacket SetupPacket,
        [In, Out] byte[] Buffer,
        uint BufferLength,
        out uint NumberOfBytesTransferred,
        IntPtr Overlapped);
}
