using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using ModernWigiDash.Service.Contracts;
using ModernWigiDash.Service.Services;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Tests;

[TestClass]
public class WcfDisplayServiceTests
{
    private static ModernWigiDashDisplayService CreateService(FrameDelivery? delivery = null)
    {
        delivery ??= new FrameDelivery(isReady: () => true);

        var touchChannel = Channel.CreateUnbounded<TouchEventInfo>();
        var transport = new DisplayHidTransport(null);
        return new ModernWigiDashDisplayService(
            transport,
            delivery,
            touchChannel.Reader,
            NullLogger<ModernWigiDashDisplayService>.Instance);
    }

    // ── SendFrame ──────────────────────────────────────────

    [TestMethod]
    public void SendFrame_NullPayload_ReturnsFalse()
    {
        var service = CreateService();

        bool result = service.SendFrame(null!);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void SendFrame_EmptyPayload_ReturnsFalse()
    {
        var service = CreateService();

        bool result = service.SendFrame(new FramePayload());

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void SendFrame_ValidPayload_QueuesWithoutCopy()
    {
        var delivered = new List<byte[]>();
        using var signal = new ManualResetEventSlim(false);
        using var delivery = new FrameDelivery(
            send: bytes =>
            {
                lock (delivered) { delivered.Add(bytes); }
                signal.Set();
                return true;
            });
        var service = CreateService(delivery);
        byte[] original = new byte[64];
        for (int i = 0; i < original.Length; i++) original[i] = (byte)i;

        bool result = service.SendFrame(new FramePayload { Data = original });

        Assert.IsTrue(result);
        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(5)), "Delivery must reach the send seam");
        byte[]? queued;
        lock (delivered) { queued = delivered.FirstOrDefault(); }
        Assert.IsNotNull(queued);
        CollectionAssert.AreEqual(original, queued);
        // Ownership contract: the service passes the deserialized array through
        // without copying (it is per-call garbage) — the perf fix for the
        // per-frame 1.2 MB LOH copy.
        Assert.AreSame(original, queued, "The frame array must be passed through by reference, not copied");
    }

    [TestMethod]
    public void SendFrame_RateLimit_RejectsBeyondPerSecondWindow()
    {
        var service = CreateService();
        byte[] frame = new byte[64];

        bool last = false;
        for (int i = 0; i < 130; i++)
        {
            last = service.SendFrame(new FramePayload { Data = frame });
        }

        Assert.IsFalse(last, "SendFrame beyond the per-second window must be rejected");
    }

    [TestMethod]
    public void SendFrame_OversizedPayload_ReturnsFalse()
    {
        var service = CreateService();
        var payload = new FramePayload
        {
            Data = new byte[DisplayProtocolConstants.FrameBufferSize * 2 + 1]
        };

        bool result = service.SendFrame(payload);

        Assert.IsFalse(result);
    }
    // ── Version / diagnostics / snapshots ──────────────────

    [TestMethod]
    public void GetVersion_ReturnsNonEmptyVersion()
    {
        var service = CreateService();

        string version = service.GetVersion();

        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
    }

    [TestMethod]
    public void GetDiagnostics_ReturnsServiceNameAndEndpoint()
    {
        var service = CreateService();

        ServiceDiagnostics diag = service.GetDiagnostics();

        Assert.AreEqual("ModernWigiDashService", diag.ServiceName);
        Assert.AreEqual("net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc", diag.WcfEndpoint);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diag.Version));
    }

    [TestMethod]
    public void GetFrameTimeSnapshot_WithoutReader_ReturnsUnavailableSnapshot()
    {
        var service = CreateService();

        FrameTimeSnapshotDto snapshot = service.GetFrameTimeSnapshot();

        Assert.IsFalse(snapshot.IsAvailable);
        Assert.IsFalse(string.IsNullOrEmpty(snapshot.ErrorMessage));
    }

    [TestMethod]
    public void GetSensorSnapshot_WithoutReader_ReturnsDisconnectedSnapshot()
    {
        var service = CreateService();

        SensorSnapshotDto snapshot = service.GetSensorSnapshot();

        Assert.IsFalse(snapshot.IsConnected);
    }

    [TestMethod]
    public void PollTouch_WithoutReader_ReturnsNull()
    {
        var service = CreateService();

        Assert.IsNull(service.PollTouch());
    }

    [TestMethod]
    public void AcquireTouchConsumer_FirstCallerWins()
    {
        var service = CreateService();

        Assert.IsTrue(service.AcquireTouchConsumer(), "The first acquisition must succeed");
        Assert.IsFalse(service.AcquireTouchConsumer(), "A second acquisition must be refused");
    }

    [TestMethod]
    public void PollTouch_BeforeAcquisition_ReturnsNull()
    {
        var service = CreateService();

        Assert.IsNull(service.PollTouch(), "Touch must not be served before a consumer asserts ownership");
    }

    // ── Data contracts ─────────────────────────────────────

    [TestMethod]
    public void NormalizeTouchType_Down_ReturnsTouchDown()
    {
        Assert.AreEqual(TouchEventType.TouchDown, DisplayHardwareWorkerService.NormalizeTouchType(DisplayProtocolConstants.TouchTypeDown));
    }

    [TestMethod]
    public void NormalizeTouchType_Up_ReturnsTouchUp()
    {
        Assert.AreEqual(TouchEventType.TouchUp, DisplayHardwareWorkerService.NormalizeTouchType(DisplayProtocolConstants.TouchTypeUp));
    }

    [TestMethod]
    public void NormalizeTouchType_Unknown_ReturnsTouchMove()
    {
        Assert.AreEqual(TouchEventType.TouchMove, DisplayHardwareWorkerService.NormalizeTouchType(0x7F));
    }

    [TestMethod]
    public void TouchEventInfo_DataContract_RoundTrips()
    {
        var info = new TouchEventInfo
        {
            Type = TouchEventType.TouchDown,
            X = 12,
            Y = 34,
            TimestampUtcTicks = 123456789
        };

        var clone = RoundTrip(info);

        Assert.AreEqual(info.Type, clone.Type);
        Assert.AreEqual(info.X, clone.X);
        Assert.AreEqual(info.Y, clone.Y);
        Assert.AreEqual(info.TimestampUtcTicks, clone.TimestampUtcTicks);
    }

    [TestMethod]
    public void DisplayStatus_DataContract_RoundTrips()
    {
        var status = new DisplayStatus
        {
            IsConnected = true,
            DevicePath = "USB\\VID_1234",
            State = "Connected",
            DiagnosticSummary = "ok",
            TotalFramesProcessed = 42
        };

        var clone = RoundTrip(status);

        Assert.IsTrue(clone.IsConnected);
        Assert.AreEqual(status.DevicePath, clone.DevicePath);
        Assert.AreEqual(status.State, clone.State);
        Assert.AreEqual(status.DiagnosticSummary, clone.DiagnosticSummary);
        Assert.AreEqual(status.TotalFramesProcessed, clone.TotalFramesProcessed);
    }

    [TestMethod]
    public void ServiceDiagnostics_Defaults_AreSafeEmpty()
    {
        var diag = new ServiceDiagnostics();

        Assert.AreEqual(string.Empty, diag.ServiceName);
        Assert.AreEqual(string.Empty, diag.ServiceAccount);
        Assert.AreEqual(string.Empty, diag.Uptime);
        Assert.AreEqual(string.Empty, diag.DisplayStatus);
        Assert.AreEqual(string.Empty, diag.WcfEndpoint);
        Assert.AreEqual(string.Empty, diag.Version);
    }

    [TestMethod]
    public void FramePayload_Defaults_ToEmptyBuffer()
    {
        var payload = new FramePayload();

        Assert.IsNotNull(payload.Data);
        Assert.AreEqual(0, payload.Data.Length);
    }

    // ── Client constants ───────────────────────────────────

    [TestMethod]
    public void Client_Defaults_AreWellFormed()
    {
        Assert.AreEqual(
            "net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc",
            ModernWigiDashDisplayServiceClient.DefaultEndpointAddress);
    }

    [TestMethod]
    public void Contract_Namespace_IsStable()
    {
        var attr = (System.ServiceModel.ServiceContractAttribute?)typeof(IModernWigiDashDisplayServiceContract)
            .GetCustomAttributes(typeof(System.ServiceModel.ServiceContractAttribute), inherit: false)
            .FirstOrDefault();

        Assert.IsNotNull(attr);
        Assert.AreEqual("http://modernwigidash.service/2024", attr.Namespace);
    }

    // ── Standby / Shutdown ─────────────────────────────────

    [TestMethod]
    public void Shutdown_WhenTransportDisconnected_ReturnsFalse_WithoutThrowing()
    {
        var service = CreateService();

        bool result = service.Shutdown();

        Assert.IsFalse(result, "Standby cannot succeed without a live device connection");
    }

    [TestMethod]
    public void Shutdown_IsIdempotent_WithoutThrowing()
    {
        var service = CreateService();

        _ = service.Shutdown();
        bool second = service.Shutdown();

        Assert.IsFalse(second);
    }

    private static T RoundTrip<T>(T value)
    {
        using var ms = new MemoryStream();
        var serializer = new DataContractSerializer(typeof(T));
        serializer.WriteObject(ms, value);
        ms.Position = 0;
        return (T)serializer.ReadObject(ms)!;
    }
}
