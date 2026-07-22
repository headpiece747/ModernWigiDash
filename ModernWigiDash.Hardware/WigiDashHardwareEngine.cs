using System;
using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Hardware;

/// <summary>
/// Alias class delegating to ModernWigidashDeviceEngine for backwards compatibility.
/// </summary>
public class WigiDashHardwareEngine : IDisposable
{
    private readonly ModernWigidashDeviceLibrary.ModernWigidashDeviceEngine _engine = new();

    public bool IsConnected => _engine.IsConnected;
    public bool IsSimulationMode => _engine.IsSimulationMode;
    public string DeviceStatus => _engine.DeviceStatus;

    public event Action<SKPoint, TouchEventType>? OnTouchEvent
    {
        add => _engine.OnTouchEvent += value;
        remove => _engine.OnTouchEvent -= value;
    }

    public bool TryConnect() => _engine.TryConnect();
    public void SendFrameBuffer(SKBitmap frameBitmap) => _engine.SendFrameBuffer(frameBitmap);
    public void SimulateTouch(float x, float y, TouchEventType eventType) => _engine.SimulateTouch(x, y, eventType);
    public void Dispose() => _engine.Dispose();
}
