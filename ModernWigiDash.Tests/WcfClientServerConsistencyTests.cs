using System.Reflection;
using ModernWigiDash.Service.Wcf;

namespace ModernWigiDash.Tests;

[TestClass]
public class WcfClientServerConsistencyTests
{
    [TestMethod]
    public void Service_ImplementsEveryContractMember()
    {
        var contract = typeof(IModernWigiDashDisplayServiceContract);
        var service = typeof(ModernWigiDashDisplayService);

        // Every contract method must be implemented by the service class.
        foreach (MethodInfo method in contract.GetMethods())
        {
            Assert.IsNotNull(
                service.GetMethod(method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray()),
                $"Service missing contract method {method.Name}");
        }
    }

    [TestMethod]
    public void Client_WrapsEveryContractOperation()
    {
        var contract = typeof(IModernWigiDashDisplayServiceContract);
        var client = typeof(ModernWigiDashDisplayServiceClient);

        // Every contract method must be exposed by the client wrapper. The
        // client intentionally adapts some signatures (e.g. SendFrame takes a
        // byte[] and builds the FramePayload), so match by name.
        foreach (MethodInfo method in contract.GetMethods())
        {
            Assert.IsNotNull(
                client.GetMethods().FirstOrDefault(m => m.Name == method.Name),
                $"Client missing wrapper for contract method {method.Name}");
        }
    }

    [TestMethod]
    public void ContractAndService_ShareServiceContractAttribute()
    {
        var contract = typeof(IModernWigiDashDisplayServiceContract);
        var service = typeof(ModernWigiDashDisplayService);

        Assert.IsTrue(service.IsAssignableTo(contract), "Service must implement the contract interface");
    }
}
