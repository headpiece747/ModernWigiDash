// <copyright file="SetupApiNative.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// SetupAPI P/Invoke for device enumeration.
/// </summary>
internal static class SetupApiNative
{
    private const string SetupApiDll = "setupapi.dll";
    private const string Kernel32Dll = "kernel32.dll";

    public const uint DigcfPresent = 0x00000002;
    public const uint DigcfDeviceInterface = 0x00000010;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x80;
    public const uint FileFlagOverlapped = 0x40000000;
    public static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// Native GUID struct for SetupAPI P/Invoke.
    /// Uses MarshalAs ByValArray for Data4 since C# doesn't allow
    /// taking the address of a field in a local struct variable.
    /// Layout is identical to SYSTEM_GUID: 16 bytes, no padding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct NativeGuid
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data4;

        public static NativeGuid FromGuid(Guid guid)
        {
            byte[] b = guid.ToByteArray();
            return new NativeGuid
            {
                Data1 = BitConverter.ToUInt32(b, 0),
                Data2 = BitConverter.ToUInt16(b, 4),
                Data3 = BitConverter.ToUInt16(b, 6),
                Data4 = b[8..]
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public NativeGuid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    /// <summary>
    /// SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize + a 1-WCHAR path placeholder.
    /// <see cref="SpDeviceInterfaceData"/>'s CbSize must equal this struct's
    /// marshaled size (Marshal.SizeOf — 8 on x64, 6 on x86), NOT the queried
    /// requiredSize, which includes the full device path and makes the call
    /// fail with ERROR_INVALID_USER_BUFFER (1784).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SpDeviceInterfaceDetailData
    {
        public uint CbSize;
        public char DevicePath;
    }

    [DllImport(SetupApiDll, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref NativeGuid ClassGuid, string? Enumerator, IntPtr HwndParent, uint Flags);

    [DllImport(SetupApiDll, EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref NativeGuid InterfaceClassGuid,
        uint MemberIndex, ref SpDeviceInterfaceData DeviceInterfaceData);

    [DllImport(SetupApiDll, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet, ref SpDeviceInterfaceData DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr RelatedDeviceInfoData);

    [DllImport(SetupApiDll, EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport(Kernel32Dll, EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport(Kernel32Dll, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}
