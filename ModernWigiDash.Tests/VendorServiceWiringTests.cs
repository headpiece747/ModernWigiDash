using System.IO;
using ModernWigiDash.App.Power;
using ModernWigiDash.Hardware.Service;

namespace ModernWigiDash.Tests;

/// <summary>
/// The vendor service wiring (ADR-0022) pinned at the window seam: the
/// VendorService startup step binds the process-wide static through the
/// injected factory, so a test host never opens a real WCF channel and the
/// reflection-instantiated widgets read the fake they were handed.
/// </summary>
[TestClass]
public class VendorServiceWiringTests
{
    private static readonly StaHost Host = new("VendorServiceWiring-STA");

    [TestCleanup]
    public void Cleanup()
    {
        VendorService.SetInstance(null);
        Host.DetachApplication();
    }

    [TestMethod]
    public void WindowConstruction_BindsTheFactoryClientToTheStatic()
    {
        var client = CreateFakeClient();
        string profilePath = Path.Combine(Path.GetTempPath(), "wmd-vendor-" + Guid.NewGuid().ToString("N"), "profile.json");

        Host.Run<object>(() =>
        {
            var window = new MainWindow(new MainWindowTestOptions(
                new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(),
                UsbEngine: FakeTransport.InertEngine(),
                VendorServiceClientFactory: () => client));
            try
            {
                Assert.AreSame(client, VendorService.Instance,
                    "the VendorService startup step must expose the factory's client through the process-wide static");
                return null;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void Teardown_ClearsTheStaticAndDisposesTheClient()
    {
        var client = CreateFakeClient();
        string profilePath = Path.Combine(Path.GetTempPath(), "wmd-vendor-" + Guid.NewGuid().ToString("N"), "profile.json");

        Host.Run<object>(() =>
        {
            var window = new MainWindow(new MainWindowTestOptions(
                new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(),
                UsbEngine: FakeTransport.InertEngine(),
                VendorServiceClientFactory: () => client));
            try
            {
                Assert.AreSame(client, VendorService.Instance);
                window.QuitClose();
                Assert.IsNull(VendorService.Instance,
                    "teardown must drop the process-wide static so no widget render reaches a disposed client");
                return null;
            }
            finally
            {
                VendorService.SetInstance(null);
            }
        });
    }

    private static WigiDashServiceClient CreateFakeClient()
    {
        return new WigiDashServiceClient(new FakeSensorValues());
    }

    private sealed class FakeSensorValues : ISensorValues
    {
        public bool IsReady => false;
        public IReadOnlyList<VendorSensorItem>? GetSensorList() => null;
        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2) => null;
    }
}
