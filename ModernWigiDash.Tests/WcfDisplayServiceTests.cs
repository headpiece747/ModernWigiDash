using System.IO;
using System.Runtime.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Service.Contracts;
using ModernWigiDash.Service.Services;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Tests;

[TestClass]
public class WcfDisplayServiceTests
{
    private static ModernWigiDashDisplayService CreateService(
        out ChannelWriter<byte[]> writer,
        out ChannelReader<byte[]> reader)
    {
        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        writer = channel.Writer;
        reader = channel.Reader;

        var touchChannel = Channel.CreateUnbounded<DisplayTouchInput>();
        var transport = new DisplayHidTransport(null);
        return new ModernWigiDashDisplayService(
            transport,
            null,
            writer,
            touchChannel.Reader,
            NullLogger<ModernWigiDashDisplayService>.Instance);
    }

    // ── SendFrame ──────────────────────────────────────────

    [TestMethod]
    public void SendFrame_NullPayload_ReturnsFalse()
    {
        var service = CreateService(out _, out _);

        bool result = service.SendFrame(null!);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void SendFrame_EmptyPayload_ReturnsFalse()
    {
        var service = CreateService(out _, out _);

        bool result = service.SendFrame(new FramePayload());

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void SendFrame_ValidPayload_QueuesCopyAndReturnsTrue()
    {
        var service = CreateService(out _, out var reader);
        byte[] original = new byte[64];
        for (int i = 0; i < original.Length; i++) original[i] = (byte)i;

        bool result = service.SendFrame(new FramePayload { Data = original });

        Assert.IsTrue(result);
        Assert.IsTrue(reader.TryRead(out byte[]? queued));
        Assert.IsNotNull(queued);
        CollectionAssert.AreEqual(original, queued);
        // The queued copy must be independent of the caller's buffer.
        original[0] = 0xFF;
        Assert.AreEqual(0, queued[0]);
    }

    [TestMethod]
    public void SendFrame_OversizedPayload_ReturnsFalse()
    {
        var service = CreateService(out _, out _);
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
        var service = CreateService(out _, out _);

        string version = service.GetVersion();

        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
    }

    [TestMethod]
    public void GetDiagnostics_ReturnsServiceNameAndEndpoint()
    {
        var service = CreateService(out _, out _);

        ServiceDiagnostics diag = service.GetDiagnostics();

        Assert.AreEqual("ModernWigiDashService", diag.ServiceName);
        Assert.AreEqual("net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc", diag.WcfEndpoint);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diag.Version));
    }

    [TestMethod]
    public void GetFrameTimeSnapshot_WithoutReader_ReturnsUnavailableSnapshot()
    {
        var service = CreateService(out _, out _);

        FrameTimeSnapshotDto snapshot = service.GetFrameTimeSnapshot();

        Assert.IsFalse(snapshot.IsAvailable);
        Assert.IsFalse(string.IsNullOrEmpty(snapshot.ErrorMessage));
    }

    [TestMethod]
    public void GetSensorSnapshot_WithoutReader_ReturnsDisconnectedSnapshot()
    {
        var service = CreateService(out _, out _);

        SensorSnapshotDto snapshot = service.GetSensorSnapshot();

        Assert.IsFalse(snapshot.IsConnected);
    }

    [TestMethod]
    public void PollTouch_WithoutReader_ReturnsNull()
    {
        var service = CreateService(out _, out _);

        Assert.IsNull(service.PollTouch());
    }

    // ── Data contracts ─────────────────────────────────────

    [TestMethod]
    public void TouchEventInfo_DataContract_RoundTrips()
    {
        var info = new TouchEventInfo
        {
            Type = 1,
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
        Assert.AreEqual("http://modernwigidash.service/2024", attr!.Namespace);
    }

    // ── Standby / Shutdown ─────────────────────────────────

    [TestMethod]
    public void Shutdown_WhenTransportDisconnected_ReturnsFalse_WithoutThrowing()
    {
        var service = CreateService(out _, out _);

        bool result = service.Shutdown();

        Assert.IsFalse(result, "Standby cannot succeed without a live device connection");
    }

    [TestMethod]
    public void Shutdown_IsIdempotent_WithoutThrowing()
    {
        var service = CreateService(out _, out _);

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
