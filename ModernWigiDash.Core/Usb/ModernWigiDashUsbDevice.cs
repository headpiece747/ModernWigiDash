using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Core.Usb;

/// <summary>
/// Modern C# .NET 10 Standalone USB Hardware Driver Engine for G.SKILL WigiDash.
/// Operates 100% independently of WigiDashDeviceLibrary.dll or G.SKILL Manager.
/// </summary>
public class ModernWigiDashUsbDevice : IDisposable
{
    public const string WigiDashDeviceGuid = "{D876A186-7B31-4804-8115-79A87E8941BD}";
    public const int ScreenWidth = 1024;
    public const int ScreenHeight = 600;

    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modern_wigidash.log");

    private object? _usbDeviceObj;
    private object? _bulkPipeObj;
    private MethodInfo? _controlTransferM;
    private MethodInfo? _controlTransferPayloadM;
    private MethodInfo? _bulkWriteM;

    private bool _isDisposed;
    private bool _isBusyStreaming = false;
    private int _framesSent = 0;
    private readonly Timer _reconnectTimer;
    private readonly Channel<byte[]> _frameChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _lock = new();

    public bool IsConnected { get; private set; } = false;
    public bool IsSimulationMode { get; private set; } = true;
    public string DeviceStatus { get; private set; } = "🟡 Initializing Hardware...";

    public event Action<SKPoint, TouchEventType>? OnTouchEvent;

    public ModernWigiDashUsbDevice()
    {
        Log("=== ModernWigiDashUsbDevice Initializing (100% Standalone .NET 10 Engine) ===");
        TryConnect();
        Task.Run(ProcessFrameQueueAsync);
        Task.Run(PollPhysicalUsbTouchAsync);
        _reconnectTimer = new Timer(_ =>
        {
            if (!_isDisposed && !IsConnected) TryConnect();
        }, null, 5000, 5000);
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); }
        catch { }
    }

    public bool TryConnect()
    {
        lock (_lock)
        {
            DisconnectInternal();
            try
            {
                // Load WinUSBNet.dll from execution directory or G.SKILL install
                string winUsbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinUSBNet.dll");
                if (!File.Exists(winUsbPath))
                    winUsbPath = @"C:\Program Files (x86)\G.SKILL\WigiDash Manager\WinUSBNet.dll";

                if (!File.Exists(winUsbPath))
                {
                    DeviceStatus = "🟡 Hardware Driver Missing (Simulation Mode)";
                    return false;
                }

                Assembly winUsbAsm = Assembly.LoadFrom(winUsbPath);
                Type? usbDevType = winUsbAsm.GetType("MadWizard.WinUSBNet.USBDevice");
                if (usbDevType == null) { Log("USBDevice type not found in WinUSBNet.dll"); return false; }

                // GetDevices returns USBDeviceInfo
                var getDevicesM = usbDevType.GetMethod("GetDevices", new Type[] { typeof(string) });
                var deviceInfoArray = getDevicesM?.Invoke(null, new object[] { WigiDashDeviceGuid }) as Array;

                if (deviceInfoArray == null || deviceInfoArray.Length == 0)
                {
                    IsConnected = false;
                    IsSimulationMode = true;
                    DeviceStatus = "🟡 Physical WigiDash Unplugged (Simulation Mode)";
                    Log("TryConnect: No WigiDash devices found via GetDevices.");
                    return false;
                }

                var deviceInfo = deviceInfoArray.GetValue(0)!;
                Log($"Found WigiDash deviceInfo: {deviceInfo.GetType().FullName}");

                // Use USBDevice(USBDeviceInfo info) constructor (same as official EUsbIf)
                var infoCtor = usbDevType.GetConstructor(new Type[] { deviceInfo.GetType() });
                if (infoCtor != null)
                {
                    Log("Opening USBDevice via USBDevice(USBDeviceInfo)...");
                    _usbDeviceObj = infoCtor.Invoke(new object[] { deviceInfo });
                }
                else
                {
                    var pathProp = deviceInfo.GetType().GetProperty("DevicePath");
                    string? devPath = pathProp?.GetValue(deviceInfo)?.ToString();
                    var pathCtor = usbDevType.GetConstructor(new Type[] { typeof(string) });
                    Log($"Opening USBDevice via path: {devPath}...");
                    _usbDeviceObj = pathCtor?.Invoke(new object[] { devPath });
                }

                if (_usbDeviceObj == null) { Log("USBDevice constructor returned null."); return false; }
                Log($"USBDevice opened successfully: {_usbDeviceObj.GetType().FullName}");

                // Find Bulk OUT Pipe
                _bulkPipeObj = FindBulkOutPipe(_usbDeviceObj);
                if (_bulkPipeObj != null)
                {
                    SetPipeTimeout(_bulkPipeObj, 5000);
                    _bulkWriteM = _bulkPipeObj.GetType().GetMethod("Write", new Type[] { typeof(byte[]) });
                    Log($"Bulk OUT Pipe ready. Write method: {(_bulkWriteM != null ? "OK" : "MISSING")}");
                }
                else
                {
                    Log("WARNING: No Bulk OUT pipe found! Frame streaming disabled.");
                }

                // Find ControlTransfer overloads
                var ctrlMethods = _usbDeviceObj.GetType().GetMethods().Where(m => m.Name == "ControlTransfer").ToArray();
                _controlTransferM = ctrlMethods.FirstOrDefault(m => m.GetParameters().Length == 4);
                _controlTransferPayloadM = ctrlMethods.FirstOrDefault(m => m.GetParameters().Length == 5)
                                        ?? ctrlMethods.FirstOrDefault(m => m.GetParameters().Length == 6);

                // G.SKILL hardware handshake
                Log("Executing hardware handshake...");
                ControlWrite(0x15, 0, null);   // ResetConfig
                ControlWrite(0x51, 100, null); // SetBrightness 100%
                ControlWrite(0x12, 0, null);   // ClearScreenTimeout
                ControlWrite(0x90, 0, null);   // ClearPage 0

                // AddWidget payload (20 bytes, wValue = 20)
                byte[] widgetPayload = new byte[20];
                widgetPayload[0] = 0; widgetPayload[1] = 0; // page 0
                widgetPayload[2] = 0; widgetPayload[3] = 0; // widget 0
                widgetPayload[6] = 0x00; widgetPayload[7] = 0x04; // width 1024
                widgetPayload[8] = 0x58; widgetPayload[9] = 0x02; // height 600
                ControlWrite(0x91, 20, widgetPayload); // AddWidget 0, wValue = 20 bytes!

                ControlWrite(0x21, 0, null); // GoToScreen 0

                IsConnected = true;
                IsSimulationMode = false;
                DeviceStatus = "🟢 Standalone .NET 10 WigiDash Display Active";
                Log("Handshake complete. Streaming frames...");
                return true;
            }
            catch (Exception ex)
            {
                var realEx = ex.InnerException ?? ex;
                Log($"Connection Exception: {realEx.Message}\n{realEx.StackTrace}");
                DisconnectInternal();
                IsConnected = false;
                IsSimulationMode = true;
                DeviceStatus = $"⚠️ Connection Error: {realEx.Message}";
                return false;
            }
        }
    }

    private void DisconnectInternal()
    {
        if (_usbDeviceObj is IDisposable disp)
        {
            try { disp.Dispose(); } catch { }
        }
        else if (_usbDeviceObj != null)
        {
            try
            {
                var dispM = _usbDeviceObj.GetType().GetMethod("Dispose", Type.EmptyTypes);
                dispM?.Invoke(_usbDeviceObj, null);
            }
            catch { }
        }
        _usbDeviceObj = null;
        _bulkPipeObj = null;
        _controlTransferM = null;
        _controlTransferPayloadM = null;
        _bulkWriteM = null;
    }

    private static object? FindBulkOutPipe(object devObj)
    {
        try
        {
            // Try top-level Pipes property first
            var pipesObj = devObj.GetType().GetProperty("Pipes")?.GetValue(devObj);
            if (pipesObj != null)
            {
                foreach (var pipe in EnumerateCollection(pipesObj))
                {
                    var addrProp = pipe.GetType().GetProperty("Address");
                    if (addrProp != null)
                    {
                        byte addr = Convert.ToByte(addrProp.GetValue(pipe));
                        Log($"  Pipe: Addr=0x{addr:X2}");
                        if (addr < 0x80) return pipe; // OUT pipe
                    }
                }
            }

            // Try Interfaces[x].Pipes
            var ifacesObj = devObj.GetType().GetProperty("Interfaces")?.GetValue(devObj);
            if (ifacesObj != null)
            {
                foreach (var iface in EnumerateCollection(ifacesObj))
                {
                    var ifacePipesObj = iface.GetType().GetProperty("Pipes")?.GetValue(iface);
                    if (ifacePipesObj != null)
                    {
                        foreach (var pipe in EnumerateCollection(ifacePipesObj))
                        {
                            var addrProp = pipe.GetType().GetProperty("Address");
                            if (addrProp != null)
                            {
                                byte addr = Convert.ToByte(addrProp.GetValue(pipe));
                                if (addr < 0x80) return pipe;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log($"FindBulkOutPipe error: {ex.Message}"); }
        return null;
    }

    private static IEnumerable<object> EnumerateCollection(object collection)
    {
        if (collection is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (item != null) yield return item;
        }
    }

    private bool ControlWrite(byte request, ushort value, byte[]? payload)
    {
        if (_usbDeviceObj == null) return false;
        try
        {
            if (payload != null && _controlTransferPayloadM != null)
            {
                var p = _controlTransferPayloadM.GetParameters();
                if (p.Length == 6)
                    _controlTransferPayloadM.Invoke(_usbDeviceObj, new object[] { (byte)0x21, request, (int)value, (int)0, payload, payload.Length });
                else
                    _controlTransferPayloadM.Invoke(_usbDeviceObj, new object[] { (byte)0x21, request, (int)value, (int)0, payload });
            }
            else if (_controlTransferM != null)
            {
                _controlTransferM.Invoke(_usbDeviceObj, new object[] { (byte)0x21, request, (int)value, (int)0 });
            }
            return true;
        }
        catch (Exception ex)
        {
            var realEx = ex.InnerException ?? ex;
            Log($"ControlWrite Warning 0x{request:X2}: {realEx.Message}");
            return false;
        }
    }

    public void SendFrameBuffer(SKBitmap frameBitmap)
    {
        if (_isDisposed || !IsConnected || frameBitmap == null) return;
        if (_isBusyStreaming) return;
        byte[] rgb565 = SKBitmapToRgb565LittleEndian(frameBitmap);
        _frameChannel.Writer.TryWrite(rgb565);
    }

    private async Task ProcessFrameQueueAsync()
    {
        while (!_isDisposed)
        {
            try
            {
                byte[] frame = await _frameChannel.Reader.ReadAsync();
                if (_isBusyStreaming) continue;
                _isBusyStreaming = true;

                try
                {
                    bool canSend;
                    lock (_lock)
                    {
                        canSend = IsConnected && _usbDeviceObj != null && _bulkWriteM != null && _bulkPipeObj != null;
                    }

                    if (canSend)
                    {
                        // Build WriteToWidget header (8 bytes): Page 0, Widget 0, Data length
                        byte[] header = new byte[8];
                        header[0] = 0; header[1] = 0; // Page 0
                        header[2] = 0; header[3] = 0; // Widget 0
                        header[4] = (byte)(frame.Length & 0xFF);
                        header[5] = (byte)((frame.Length >> 8) & 0xFF);
                        header[6] = (byte)((frame.Length >> 16) & 0xFF);
                        header[7] = (byte)((frame.Length >> 24) & 0xFF);

                        // Send WriteToWidget Header (0x61, wValue = 8 = header byte size!)
                        lock (_lock) { ControlWrite(0x61, 8, header); }

                        // Bulk stream frame data over USB Pipe 0x01
                        try
                        {
                            _bulkWriteM!.Invoke(_bulkPipeObj, new object[] { frame });
                        }
                        catch (Exception bex)
                        {
                            var realEx = bex.InnerException ?? bex;
                            Log($"Bulk Write FAILED: {realEx.Message}");
                        }

                        await Task.Delay(20);

                        // Commit Frame (0x63, wValue = 0)
                        lock (_lock) { ControlWrite(0x63, 0, null); }

                        _framesSent++;
                        if (_framesSent <= 3 || _framesSent % 60 == 0)
                            Log($"Frame #{_framesSent} sent ({frame.Length} bytes)");
                    }
                }
                finally { _isBusyStreaming = false; }

                await Task.Delay(33); // ~30 FPS
            }
            catch (Exception ex)
            {
                _isBusyStreaming = false;
                var realEx = ex.InnerException ?? ex;
                Log($"ProcessFrameQueue error: {realEx.Message}");
                await Task.Delay(500);
            }
        }
    }

    private static void SetPipeTimeout(object pipeObj, int timeoutMs)
    {
        try
        {
            var policyProp = pipeObj.GetType().GetProperty("Policy");
            var policyObj = policyProp?.GetValue(pipeObj);
            if (policyObj != null)
            {
                var timeoutProp = policyObj.GetType().GetProperty("PipeTransferTimeout");
                timeoutProp?.SetValue(policyObj, timeoutMs);
            }
        }
        catch { }
    }

    private static byte[] SKBitmapToRgb565LittleEndian(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] rgb565 = new byte[width * height * 2];

        using var pixmap = bitmap.PeekPixels();
        unsafe
        {
            byte* srcPtr = (byte*)pixmap.GetPixels();
            fixed (byte* dstPtr = rgb565)
            {
                ushort* dstUshort = (ushort*)dstPtr;
                int pixelCount = width * height;
                for (int i = 0; i < pixelCount; i++)
                {
                    byte b = srcPtr[i * 4];
                    byte g = srcPtr[i * 4 + 1];
                    byte r = srcPtr[i * 4 + 2];
                    dstUshort[i] = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
                }
            }
        }
        return rgb565;
    }

    public void SimulateTouch(float x, float y, TouchEventType eventType)
        => OnTouchEvent?.Invoke(new SKPoint(x, y), eventType);

    private async Task PollPhysicalUsbTouchAsync()
    {
        byte[] touchBuf = new byte[8];
        while (!_isDisposed)
        {
            try
            {
                if (IsConnected && _usbDeviceObj != null && _controlTransferPayloadM != null)
                {
                    var p = _controlTransferPayloadM.GetParameters();
                    object? result;
                    if (p.Length == 6)
                        result = _controlTransferPayloadM.Invoke(_usbDeviceObj, new object[] { (byte)0xC0, (byte)0xA0, (int)0, (int)0, touchBuf, touchBuf.Length });
                    else
                        result = _controlTransferPayloadM.Invoke(_usbDeviceObj, new object[] { (byte)0xC0, (byte)0xA0, (int)0, (int)0, touchBuf });

                    int bytesRead = result is int ir ? ir : 0;
                    if (bytesRead >= 5)
                    {
                        byte actionByte = touchBuf[0];
                        ushort x = BitConverter.ToUInt16(touchBuf, 1);
                        ushort y = BitConverter.ToUInt16(touchBuf, 3);
                        if (actionByte != 0 && (x > 0 || y > 0 || actionByte == 3))
                        {
                            TouchEventType type = actionByte == 1 ? TouchEventType.TouchDown
                                                : actionByte == 2 ? TouchEventType.TouchMove
                                                : TouchEventType.TouchUp;
                            Log($"Touch: Action={actionByte} X={x} Y={y}");
                            OnTouchEvent?.Invoke(new SKPoint(x, y), type);
                        }
                    }
                }
            }
            catch { }
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _reconnectTimer.Dispose();
        _frameChannel.Writer.TryComplete();
        lock (_lock)
        {
            IsConnected = false;
            IsSimulationMode = true;
            DisconnectInternal();
        }
    }
}
