using System;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Usb;

namespace ModernWigiDash.Core.Usb;

/// <summary>
/// Wraps ModernWigiDashUsbDevice — the proven 100% working .NET 10 WinUSB engine.
/// </summary>
public class WigiDashHidDevice : IDisposable
{
    private readonly ModernWigiDashUsbDevice _engine;

    public bool IsConnected => _engine.IsConnected;
    public bool IsSimulationMode => _engine.IsSimulationMode;
    public string DeviceStatus => _engine.DeviceStatus;

    public event Action<SKPoint, TouchEventType>? OnTouchEvent
    {
        add => _engine.OnTouchEvent += value;
        remove => _engine.OnTouchEvent -= value;
    }

    public WigiDashHidDevice()
    {
        _engine = new ModernWigiDashUsbDevice();
    }

    public bool TryConnect() => _engine.TryConnect();

    public void SendFrameBuffer(SKBitmap frameBitmap) => _engine.SendFrameBuffer(frameBitmap);

    public void SimulateTouch(float x, float y, TouchEventType eventType) => _engine.SimulateTouch(x, y, eventType);

    public void Dispose() => _engine.Dispose();
}
