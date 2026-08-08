using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using ModernWigiDash.Service;
using ModernWigiDash.Service.Services;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Tests;

[TestClass]
public class ServiceHostSmokeTests
{
    [TestMethod]
    public void CreateBuilder_ResolvesEverySingletonRegistration()
    {
        using var app = Program.CreateBuilder([], isTestMode: true).Build();

        Assert.IsNotNull(app.Services.GetRequiredService<IDisplayTransport>());
        Assert.IsNotNull(app.Services.GetRequiredService<FrameDelivery>());
        Assert.IsNotNull(app.Services.GetRequiredService<TimeProvider>());
        Assert.IsNotNull(app.Services.GetRequiredService<DisplayHardwareWorkerService>());
        Assert.IsNotNull(app.Services.GetRequiredService<LhmSensorReader>());
        Assert.IsNotNull(app.Services.GetRequiredService<FrameTimeReader>());
        Assert.IsNotNull(app.Services.GetRequiredService<ModernWigiDashDisplayService>());
    }

    [TestMethod]
    public void CreateBuilder_RegistersExactlyOneWorkerPerHostedService()
    {
        using var app = Program.CreateBuilder([], isTestMode: true).Build();
        var hosted = app.Services.GetServices<IHostedService>().ToArray();

        // The framework adds its own hosted services (lifetime, etc.) — the
        // contract here is one instance per worker type, no duplicates.
        Assert.IsTrue(hosted.Length >= 3);
        Assert.IsTrue(hosted.OfType<DisplayHardwareWorkerService>().Count() == 1, "The hardware worker must register exactly once");
        Assert.IsTrue(hosted.OfType<LhmSensorReader>().Count() == 1);
        Assert.IsTrue(hosted.OfType<FrameTimeReader>().Count() == 1);
    }

    [TestMethod]
    public void CreateBuilder_TouchChannelIsSharedSingleton()
    {
        using var app = Program.CreateBuilder([], isTestMode: true).Build();
        var provider = app.Services;

        Assert.AreSame(
            provider.GetRequiredService<System.Threading.Channels.ChannelWriter<TouchEventInfo>>(),
            provider.GetRequiredService<System.Threading.Channels.ChannelWriter<TouchEventInfo>>(),
            "The touch channel writer must be a single shared instance");
        Assert.AreSame(
            provider.GetRequiredService<System.Threading.Channels.ChannelReader<TouchEventInfo>>(),
            provider.GetRequiredService<System.Threading.Channels.ChannelReader<TouchEventInfo>>());
    }

    [TestMethod]
    public void CreateBuilder_ServiceIsPerCallButStateIsSingleton()
    {
        using var app = Program.CreateBuilder([], isTestMode: true).Build();
        var provider = app.Services;

        // PerCall lifecycle regression: mutable cross-call state (rate limits,
        // touch ownership) must live on the injected singleton, never on the
        // per-request service instance — otherwise every guard is inert.
        // Resolve through two scopes (a WCF call is one scope).
        using (var scope1 = provider.CreateScope())
        using (var scope2 = provider.CreateScope())
        {
            var first = scope1.ServiceProvider.GetRequiredService<ModernWigiDashDisplayService>();
            var second = scope2.ServiceProvider.GetRequiredService<ModernWigiDashDisplayService>();
            Assert.AreNotSame(first, second, "The service must be PerCall");

            var state1 = scope1.ServiceProvider.GetRequiredService<ServiceCallState>();
            var state2 = scope2.ServiceProvider.GetRequiredService<ServiceCallState>();
            Assert.AreSame(state1, state2, "Call state must be a singleton shared by every request instance");
        }
    }
}
