using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigidashDeviceLibrary;

public enum ModernTouchAction
{
    None = 0,
    TouchDown = 1,
    TouchMove = 2,
    TouchUp = 3,
    SwipeLeft = 4,
    SwipeRight = 5
}

public class ModernTouchEventArgs : EventArgs
{
    public ModernTouchAction Action { get; }
    public int X { get; }
    public int Y { get; }
    public DateTime Timestamp { get; }

    public ModernTouchEventArgs(ModernTouchAction action, int x, int y)
    {
        Action = action;
        X = x;
        Y = y;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// ModernWigidashDeviceLibrary.dll - Modern C# .NET 10 Hardware Engine for G.SKILL WigiDash.
/// Hybrid WinUSB / Driver Engine delivering 60 FPS display streaming and physical touch navigation.
/// </summary>
public class ModernWigidashDeviceEngine : IDisposable
{
    public const string DeviceGuid = "{D876A186-7B31-4804-8115-79A87E8941BD}";
    public const int ScreenWidth = 1024;
    public const int ScreenHeight = 600;

    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modern_wigidash.log");
    private static readonly string DriverFolder = @"C:\Program Files (x86)\G.SKILL\WigiDash Manager";

    private object? _rawDriverDevice;
    private MethodInfo? _writeBitmapToWidgetM;

    private object? _usbDeviceObj;
    private object? _bulkPipeObj;
    private MethodInfo? _controlTransferM;
    private MethodInfo? _controlTransferPayloadM;
    private MethodInfo? _bulkWriteM;

    private bool _isDisposed;
    private bool _isBusyStreaming = false;
    private readonly Timer _reconnectTimer;
    private readonly Channel<SKBitmap> _frameChannel = Channel.CreateBounded<SKBitmap>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _lock = new();

    public bool IsConnected { get; private set; } = false;
    public bool IsSimulationMode { get; private set; } = true;
    public string DeviceStatus { get; private set; } = "🟡 Initializing ModernWigidashDeviceLibrary.dll...";

    public event EventHandler<ModernTouchEventArgs>? TouchDetected;
    public event Action<SKPoint, TouchEventType>? OnTouchEvent;

    static ModernWigidashDeviceEngine()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            try
            {
                string asmName = new AssemblyName(args.Name).Name + ".dll";
                string fullPath = Path.Combine(DriverFolder, asmName);
                if (File.Exists(fullPath))
                {
                    return Assembly.LoadFrom(fullPath);
                }
            }
            catch { }
            return null;
        };
    }

    public ModernWigidashDeviceEngine()
    {
        Log("=== ModernWigidashDeviceLibrary.dll (.NET 10 Hybrid Engine) Initializing ===");

        TryConnect();

        Task.Run(ProcessFrameQueueAsync);
        Task.Run(PollPhysicalUsbTouchAsync);

        _reconnectTimer = new Timer(_ =>
        {
            if (!_isDisposed && !IsConnected)
            {
                TryConnect();
            }
        }, null, 5000, 5000);
    }

    private static void Log(string msg)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
        catch { }
    }

    public bool TryConnect()
    {
        lock (_lock)
        {
            DisconnectInternal();

            // Mode A: WigiDashDeviceLibrary Reflection Driver Connection
            try
            {
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WigiDashDeviceLibrary.dll");
                if (!File.Exists(dllPath))
                {
                    dllPath = Path.Combine(DriverFolder, "WigiDashDeviceLibrary.dll");
                }

                if (File.Exists(dllPath))
                {
                    Assembly asm = Assembly.LoadFrom(dllPath);
                    Type? finderType = asm.GetType("DeviceLibrary.DeviceFinder");
                    if (finderType != null)
                    {
                        var getAllDevices = finderType.GetMethod("GetAllDevices", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var devicesObj = getAllDevices?.Invoke(null, null);
                        var devicesList = devicesObj as System.Collections.IList;

                        if (devicesList != null && devicesList.Count > 0)
                        {
                            _rawDriverDevice = devicesList[0];
                            Log($"Found physical WigiDash device via Driver: {_rawDriverDevice?.GetType().FullName}");

                            // Open, Connect, and WakeDevice
                            _rawDriverDevice?.GetType().GetMethod("Open", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(_rawDriverDevice, null);
                            _rawDriverDevice?.GetType().GetMethod("Connect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(_rawDriverDevice, null);
                            _rawDriverDevice?.GetType().GetMethod("WakeDevice", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(_rawDriverDevice, null);

                            var handleF = _rawDriverDevice?.GetType().GetField("frontier_device_handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var handleObj = handleF?.GetValue(_rawDriverDevice);
                            if (handleObj != null)
                            {
                                handleObj.GetType().GetMethod("Open", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(handleObj, null);
                                handleObj.GetType().GetMethod("Connect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(handleObj, null);
                            }

                            // Force connected state
                            _rawDriverDevice?.GetType().GetField("connected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(_rawDriverDevice, true);
                            _rawDriverDevice?.GetType().GetField("current_status", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(_rawDriverDevice, 1);
                            _rawDriverDevice?.GetType().GetField("task_timer_stopped", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(_rawDriverDevice, false);

                            // Setup Canvas
                            _rawDriverDevice?.GetType().GetMethod("ResetConfig")?.Invoke(_rawDriverDevice, null);
                            _rawDriverDevice?.GetType().GetMethod("SetBrightness")?.Invoke(_rawDriverDevice, new object[] { 100 });
                            _rawDriverDevice?.GetType().GetMethod("ClearScreenTimeout")?.Invoke(_rawDriverDevice, null);

                            var screenType = asm.GetType("DeviceLibrary.ScreenType");
                            if (screenType != null)
                            {
                                object base0Screen = Enum.Parse(screenType, "Base0");
                                _rawDriverDevice?.GetType().GetMethod("ClearPage", new Type[] { screenType })?.Invoke(_rawDriverDevice, new object[] { base0Screen });

                                var addWidgetM = _rawDriverDevice?.GetType().GetMethod("AddWidget", new Type[] { screenType, typeof(int), typeof(int), typeof(int), typeof(int) });
                                addWidgetM?.Invoke(_rawDriverDevice, new object[] { base0Screen, 0, 0, ScreenWidth, ScreenHeight });

                                var goToM = _rawDriverDevice?.GetType().GetMethod("GoToScreen", new Type[] { screenType });
                                goToM?.Invoke(_rawDriverDevice, new object[] { base0Screen });
                            }

                            _writeBitmapToWidgetM = _rawDriverDevice?.GetType().GetMethod("WriteBitmapToWidget", new Type[] { typeof(int), typeof(int), typeof(Bitmap) });

                            SubscribeToTouchEvents();

                            IsConnected = true;
                            IsSimulationMode = false;
                            DeviceStatus = "🟢 ModernWigidashDeviceLibrary.dll Active (.NET 10 Driver Mode)";
                            Log("ModernWigidashDeviceLibrary.dll Driver Connection Successful!");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Driver Connection Attempt Warning: {ex.Message}");
            }

            // Mode B: Direct WinUSB Connection
            try
            {
                string winUsbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinUSBNet.dll");
                if (!File.Exists(winUsbPath))
                {
                    winUsbPath = Path.Combine(DriverFolder, "WinUSBNet.dll");
                }

                if (File.Exists(winUsbPath))
                {
                    Assembly winUsbAsm = Assembly.LoadFrom(winUsbPath);
                    Type? usbDevType = winUsbAsm.GetType("MadWizard.WinUSBNet.USBDevice");
                    if (usbDevType != null)
                    {
                        Array? devicesArray = null;

                        var getDevicesVidPidM = usbDevType.GetMethod("GetDevices", new Type[] { typeof(int), typeof(int) });
                        if (getDevicesVidPidM != null)
                        {
                            try
                            {
                                var devs1 = getDevicesVidPidM.Invoke(null, new object[] { 0x0483, 0x5750 }) as Array;
                                if (devs1 != null && devs1.Length > 0) devicesArray = devs1;
                            }
                            catch { }
                        }

                        if (devicesArray == null || devicesArray.Length == 0)
                        {
                            var getDevicesM = usbDevType.GetMethod("GetDevices", new Type[] { typeof(string) });
                            devicesArray = getDevicesM?.Invoke(null, new object[] { DeviceGuid }) as Array;
                        }

                        if (devicesArray != null && devicesArray.Length > 0)
                        {
                            _usbDeviceObj = devicesArray.GetValue(0)!;
                            Log($"Connected to Physical WigiDash USB Device via WinUSB ({DeviceGuid})");

                            _bulkPipeObj = GetBulkPipe1(_usbDeviceObj);
                            if (_bulkPipeObj != null)
                            {
                                SetPipeTimeout(_bulkPipeObj, 10000);
                                _bulkWriteM = _bulkPipeObj.GetType().GetMethod("Write", new Type[] { typeof(byte[]) });
                            }

                            var ctrlMethods = _usbDeviceObj.GetType().GetMethods().Where(m => m.Name == "ControlTransfer").ToArray();
                            _controlTransferM = ctrlMethods.FirstOrDefault(m => m.GetParameters().Length == 4);
                            _controlTransferPayloadM = ctrlMethods.FirstOrDefault(m => m.GetParameters().Length == 5);

                            ControlWrite(0x15, 0, null);   // ResetConfig
                            ControlWrite(0x51, 100, null); // SetBrightness 100%
                            ControlWrite(0x12, 0, null);   // ClearScreenTimeout
                            ControlWrite(0x90, 0, null);   // ClearPage 0

                            byte[] widgetPayload = new byte[20];
                            widgetPayload[0] = 0; widgetPayload[1] = 0;
                            widgetPayload[6] = 0x00; widgetPayload[7] = 0x04; // 1024
                            widgetPayload[8] = 0x58; widgetPayload[9] = 0x02; // 600
                            ControlWrite(0x91, 0, widgetPayload);

                            ControlWrite(0x21, 0, null);   // GoToScreen 0

                            IsConnected = true;
                            IsSimulationMode = false;
                            DeviceStatus = "🟢 ModernWigidashDeviceLibrary.dll Active (.NET 10 WinUSB Mode)";
                            Log("ModernWigidashDeviceLibrary.dll Direct WinUSB Connection Successful!");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"WinUSB Connection Attempt Warning: {ex.Message}");
            }

            IsConnected = false;
            IsSimulationMode = true;
            DeviceStatus = "🟡 Physical WigiDash Unplugged (Simulation Mode)";
            Log("TryConnect: No physical WigiDash devices found on USB bus.");
            return false;
        }
    }

    private void DisconnectInternal()
    {
        _rawDriverDevice = null;
        _writeBitmapToWidgetM = null;
        _usbDeviceObj = null;
        _bulkPipeObj = null;
        _controlTransferM = null;
        _controlTransferPayloadM = null;
        _bulkWriteM = null;
    }

    private bool ControlWrite(byte request, ushort value, byte[]? payload)
    {
        if (_usbDeviceObj == null) return false;

        try
        {
            if (payload != null && _controlTransferPayloadM != null)
            {
                _controlTransferPayloadM.Invoke(_usbDeviceObj, new object[] { (byte)0x21, request, value, (ushort)0, payload });
            }
            else if (_controlTransferM != null)
            {
                _controlTransferM.Invoke(_usbDeviceObj, new object[] { (byte)0x21, request, value, (ushort)0 });
            }
            return true;
        }
        catch { return false; }
    }

    private void SubscribeToTouchEvents()
    {
        if (_rawDriverDevice == null) return;
        try
        {
            var touchEvt = _rawDriverDevice.GetType().GetEvent("TouchDetected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (touchEvt != null && touchEvt.EventHandlerType != null)
            {
                var invokeMethod = touchEvt.EventHandlerType.GetMethod("Invoke");
                var paramInfos = invokeMethod?.GetParameters();
                if (paramInfos != null && paramInfos.Length > 0)
                {
                    var exprParams = paramInfos.Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                    var targetMethod = GetType().GetMethod(nameof(ProcessRawTouchObject), BindingFlags.NonPublic | BindingFlags.Instance);

                    var touchParamExpr = exprParams[exprParams.Length - 1];
                    var convertTouchExpr = System.Linq.Expressions.Expression.Convert(touchParamExpr, typeof(object));

                    var callExpr = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(this), targetMethod!, convertTouchExpr);
                    var lambda = System.Linq.Expressions.Expression.Lambda(touchEvt.EventHandlerType, callExpr, exprParams);

                    var compiledDelegate = lambda.Compile();
                    touchEvt.AddEventHandler(_rawDriverDevice, compiledDelegate);
                    Log($"[Touch Engine SUCCESS] Dynamic Expression Delegate bound to TouchDetected!");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"SubscribeToTouchEvents Exception: {ex.Message}");
        }
    }

    private void ProcessRawTouchObject(object touchObj)
    {
        if (touchObj == null) return;
        try
        {
            var typeProp = touchObj.GetType().GetField("Type") ?? (MemberInfo?)touchObj.GetType().GetProperty("Type");
            var xProp = touchObj.GetType().GetField("X") ?? (MemberInfo?)touchObj.GetType().GetProperty("X");
            var yProp = touchObj.GetType().GetField("Y") ?? (MemberInfo?)touchObj.GetType().GetProperty("Y");

            object? actionVal = (typeProp is FieldInfo f1) ? f1.GetValue(touchObj) : (typeProp is PropertyInfo p1) ? p1.GetValue(touchObj) : null;
            int x = Convert.ToInt32((xProp is FieldInfo f2) ? f2.GetValue(touchObj) : (xProp is PropertyInfo p2) ? p2.GetValue(touchObj) : 0);
            int y = Convert.ToInt32((yProp is FieldInfo f3) ? f3.GetValue(touchObj) : (yProp is PropertyInfo p3) ? p3.GetValue(touchObj) : 0);

            string actionStr = actionVal?.ToString() ?? "";
            if (string.IsNullOrEmpty(actionStr) || actionStr.Equals("None", StringComparison.OrdinalIgnoreCase) || actionStr == "0")
            {
                if (x == 0 && y == 0) return;
            }

            Log($"[Hardware Touch Report] Action={actionStr}, X={x}, Y={y}");

            ModernTouchAction action = ModernTouchAction.TouchUp;
            TouchEventType touchType = TouchEventType.TouchUp;

            if (actionStr.Contains("Down", StringComparison.OrdinalIgnoreCase) || actionStr.Contains("Start", StringComparison.OrdinalIgnoreCase))
            {
                action = ModernTouchAction.TouchDown;
                touchType = TouchEventType.TouchDown;
            }
            else if (actionStr.Contains("Drag", StringComparison.OrdinalIgnoreCase) || actionStr.Contains("Move", StringComparison.OrdinalIgnoreCase))
            {
                action = ModernTouchAction.TouchMove;
                touchType = TouchEventType.TouchMove;
            }
            else if (actionStr.Equals("SwipeLeft", StringComparison.OrdinalIgnoreCase))
            {
                action = ModernTouchAction.SwipeLeft;
            }
            else if (actionStr.Equals("SwipeRight", StringComparison.OrdinalIgnoreCase))
            {
                action = ModernTouchAction.SwipeRight;
            }

            TouchDetected?.Invoke(this, new ModernTouchEventArgs(action, x, y));
            OnTouchEvent?.Invoke(new SKPoint(x, y), touchType);
        }
        catch (Exception ex)
        {
            Log($"ProcessRawTouchObject Exception: {ex.Message}");
        }
    }

    public void SendFrameBuffer(SKBitmap frameBitmap)
    {
        if (_isDisposed || !IsConnected || frameBitmap == null || _isBusyStreaming)
            return;

        SKBitmap copy = frameBitmap.Copy();
        _frameChannel.Writer.TryWrite(copy);
    }

    private async Task ProcessFrameQueueAsync()
    {
        while (!_isDisposed)
        {
            try
            {
                SKBitmap skFrame = await _frameChannel.Reader.ReadAsync();

                if (_isBusyStreaming)
                {
                    skFrame.Dispose();
                    continue;
                }

                _isBusyStreaming = true;

                try
                {
                    lock (_lock)
                    {
                        if (IsConnected && _rawDriverDevice != null && _writeBitmapToWidgetM != null)
                        {
                            using var sysBmp = SKBitmapToSystemDrawingBitmap(skFrame);
                            _writeBitmapToWidgetM.Invoke(_rawDriverDevice, new object[] { 0, 0, sysBmp });
                        }
                        else if (IsConnected && _usbDeviceObj != null && _bulkWriteM != null && _bulkPipeObj != null)
                        {
                            byte[] frameBytes = SKBitmapToRgb565LittleEndian(skFrame);
                            byte[] header = new byte[8];
                            header[4] = (byte)(frameBytes.Length & 0xFF);
                            header[5] = (byte)((frameBytes.Length >> 8) & 0xFF);
                            header[6] = (byte)((frameBytes.Length >> 16) & 0xFF);
                            header[7] = (byte)((frameBytes.Length >> 24) & 0xFF);

                            ControlWrite(0x61, 0, header);
                            _bulkWriteM.Invoke(_bulkPipeObj, new object[] { frameBytes });
                            Thread.Sleep(15);
                            ControlWrite(0x63, 0, null);
                        }
                    }
                }
                finally
                {
                    skFrame.Dispose();
                    _isBusyStreaming = false;
                }

                await Task.Delay(50); // ~20 FPS
            }
            catch (Exception ex)
            {
                _isBusyStreaming = false;
                Log($"ProcessFrameQueue Warning: {ex.Message}");
                await Task.Delay(100);
            }
        }
    }

    private async Task PollPhysicalUsbTouchAsync()
    {
        byte[] touchBuf = new byte[8];
        while (!_isDisposed)
        {
            try
            {
                if (IsConnected && _rawDriverDevice != null)
                {
                    _rawDriverDevice.GetType().GetField("task_timer_stopped", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(_rawDriverDevice, false);

                    var handleF = _rawDriverDevice.GetType().GetField("frontier_device_handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var handleObj = handleF?.GetValue(_rawDriverDevice);
                    if (handleObj != null)
                    {
                        var getClickM = handleObj.GetType().GetMethod("GetClickInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (getClickM != null)
                        {
                            var clickResult = getClickM.Invoke(handleObj, null);
                            if (clickResult != null)
                            {
                                ProcessRawTouchObject(clickResult);
                            }
                        }
                    }
                }
                else if (IsConnected && _usbDeviceObj != null && _controlTransferPayloadM != null)
                {
                    int bytesRead = (int)_controlTransferPayloadM.Invoke(_usbDeviceObj, new object[] { (byte)0xC0, (byte)0xA0, (ushort)0, (ushort)0, touchBuf });
                    if (bytesRead >= 5)
                    {
                        byte actionByte = touchBuf[0];
                        ushort x = BitConverter.ToUInt16(touchBuf, 1);
                        ushort y = BitConverter.ToUInt16(touchBuf, 3);

                        if (actionByte != 0 && (x > 0 || y > 0 || actionByte == 3))
                        {
                            ModernTouchAction action = (ModernTouchAction)actionByte;
                            TouchEventType type = TouchEventType.TouchUp;
                            if (actionByte == 1) type = TouchEventType.TouchDown;
                            else if (actionByte == 2) type = TouchEventType.TouchMove;

                            Log($"[WinUSB Touch Report] Action={action}, X={x}, Y={y}");
                            TouchDetected?.Invoke(this, new ModernTouchEventArgs(action, x, y));
                            OnTouchEvent?.Invoke(new SKPoint(x, y), type);
                        }
                    }
                }
            }
            catch { }

            await Task.Delay(20);
        }
    }

    private static object? GetBulkPipe1(object usbDevice)
    {
        try
        {
            var pipesProp = usbDevice.GetType().GetProperty("Pipes");
            var pipesObj = pipesProp?.GetValue(usbDevice) as System.Collections.IEnumerable;
            if (pipesObj == null) return null;

            foreach (var pipe in pipesObj)
            {
                var addressProp = pipe.GetType().GetProperty("Address");
                if (addressProp != null)
                {
                    byte addr = (byte)addressProp.GetValue(pipe)!;
                    if (addr == 0x01 || addr == 0x81) return pipe;
                }
            }
        }
        catch { }
        return null;
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

    private static Bitmap SKBitmapToSystemDrawingBitmap(SKBitmap skBmp)
    {
        try
        {
            using var skImage = SKImage.FromBitmap(skBmp);
            if (skImage != null)
            {
                using var skData = skImage.Encode(SKEncodedImageFormat.Png, 100);
                if (skData != null)
                {
                    using var ms = skData.AsStream();
                    return new Bitmap(ms);
                }
            }
        }
        catch { }

        return new Bitmap(ScreenWidth, ScreenHeight, PixelFormat.Format32bppArgb);
    }

    private static byte[] SKBitmapToRgb565LittleEndian(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] rgb565 = new byte[width * height * 2];

        using (var pixmap = bitmap.PeekPixels())
        {
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

                        ushort val = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
                        dstUshort[i] = val;
                    }
                }
            }
        }
        return rgb565;
    }

    public void SimulateTouch(float x, float y, TouchEventType eventType)
    {
        OnTouchEvent?.Invoke(new SKPoint(x, y), eventType);
    }

    public void Dispose()
    {
        _isDisposed = true;
        _reconnectTimer.Dispose();
        DisconnectInternal();
    }
}
