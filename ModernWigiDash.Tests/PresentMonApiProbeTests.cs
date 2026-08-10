using System.Runtime.InteropServices;
using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

[TestClass]
public class PresentMonApiProbeTests
{
    [TestMethod]
    public void Probe_NoLibraryOnDisk_ReportsNotFoundReason()
    {
        var loader = new FakeLoader(load: _ => (IntPtr?)null, failureReason: null);

        var probe = new PresentMonApiProbe(loader);

        Assert.IsNull(probe.Library);
        Assert.IsNotNull(probe.FailureReason);
        StringAssert.Contains(probe.FailureReason, "not found", "A missing library must map to the install hint, not a version complaint");
        StringAssert.Contains(probe.FailureReason, "PresentMon Service");
    }

    [TestMethod]
    public void Probe_MissingRequiredExport_ReportsIncompatibleVersion()
    {
        // Every export except pmGetApiVersion — the version gate must never run.
        var loader = new FakeLoader(load: _ => (IntPtr?)0x10, failureReason: null);
        loader.AddExports(
            "pmOpenSession", "pmCloseSession", "pmStartTrackingProcess",
            "pmRegisterDynamicQuery", "pmFreeDynamicQuery", "pmPollDynamicQuery",
            "pmRegisterFrameQuery", "pmConsumeFrames", "pmFreeFrameQuery",
            "pmGetIntrospectionRoot", "pmFreeIntrospectionRoot");

        var probe = new PresentMonApiProbe(loader);

        Assert.IsNotNull(probe.FailureReason);
        StringAssert.Contains(probe.FailureReason, "missing required exports");
    }

    [TestMethod]
    public void Probe_NonV3ApiGeneration_ReportsUnsupportedVersion()
    {
        var loader = new FakeLoader(load: _ => (IntPtr?)0x10, failureReason: null);
        loader.AddExports(
            "pmOpenSession", "pmCloseSession", "pmStartTrackingProcess",
            "pmRegisterDynamicQuery", "pmFreeDynamicQuery", "pmPollDynamicQuery",
            "pmRegisterFrameQuery", "pmConsumeFrames", "pmFreeFrameQuery",
            "pmGetIntrospectionRoot", "pmFreeIntrospectionRoot");
        loader.SetApiVersion(major: 2, minor: 5, patch: 1);

        var probe = new PresentMonApiProbe(loader);

        Assert.IsNotNull(probe.FailureReason);
        StringAssert.Contains(probe.FailureReason, "version 2.5.1");
        StringAssert.Contains(probe.FailureReason, "v3.x required");
    }

    [TestMethod]
    public void Probe_CandidateExistsButCannotLoad_ReportsLoadFailure()
    {
        var loader = new FakeLoader(
            load: _ => throw new DllNotFoundException("bad image"),
            failureReason: "PresentMonAPI2.dll at 'C:\\x\\PresentMonAPI2.dll' could not be loaded.");

        var probe = new PresentMonApiProbe(loader);

        Assert.IsNull(probe.Library);
        Assert.AreEqual("PresentMonAPI2.dll at 'C:\\x\\PresentMonAPI2.dll' could not be loaded.", probe.FailureReason);
    }

    [TestMethod]
    public void Probe_Version3Api_ReportsAvailable()
    {
        var loader = new FakeLoader(load: _ => (IntPtr?)0x10, failureReason: null);
        loader.AddExports(
            "pmOpenSession", "pmCloseSession", "pmStartTrackingProcess",
            "pmRegisterDynamicQuery", "pmFreeDynamicQuery", "pmPollDynamicQuery",
            "pmRegisterFrameQuery", "pmConsumeFrames", "pmFreeFrameQuery",
            "pmGetIntrospectionRoot", "pmFreeIntrospectionRoot");
        loader.SetApiVersion(major: 3, minor: 0, patch: 3);

        var probe = new PresentMonApiProbe(loader);

        Assert.IsNotNull(probe.Library);
        Assert.IsNull(probe.FailureReason, "A v3 library with every export must load cleanly");
        Assert.IsNotNull(probe.OpenSessionFn);
        Assert.IsNotNull(probe.GetApiVersionFn);
    }

    [TestMethod]
    public void PmVersion_MarshalledSize_MatchesNativeStruct()
    {
        // PM_VERSION is 6 bytes of version ushorts + 34 bytes of build strings.
        // A smaller mirror made pmGetApiVersion overrun the stack buffer by 34
        // bytes and fail-fast 0xC0000409 at the caller's return (stack cookie).
        Assert.AreEqual(40, Marshal.SizeOf<PmVersion>());
    }

    /// <summary>
    /// Fake over the platform seam: fabricates a module handle and resolves
    /// exports from a name table, so the probe's policy branches run without
    /// the real PresentMonAPI2.dll.
    /// </summary>
    private sealed class FakeLoader : IPresentMonLibraryLoader
    {
        private readonly Func<string[], IntPtr?> _load;
        private readonly string? _failureReason;
        private readonly HashSet<string> _exports = [];
        private IntPtr _apiVersionPointer;

        public FakeLoader(Func<string[], IntPtr?> load, string? failureReason)
        {
            _load = load;
            _failureReason = failureReason;
        }

        public void AddExports(params string[] names)
        {
            foreach (string name in names)
            {
                _exports.Add(name);
            }
        }

        public void SetApiVersion(ushort major, ushort minor, ushort patch)
        {
            // A real function pointer is required for the probe to invoke the
            // version gate. Delegate keeps the struct filled; no native call.
            var fn = new PmGetApiVersion(delegate (out PmVersion version)
            {
                version = new PmVersion
                {
                    Major = major,
                    Minor = minor,
                    Patch = patch,
                    Tag = new byte[22],
                    Hash = new byte[8],
                    Config = new byte[4],
                };
                return PmStatus.Success;
            });
            _apiVersionPointer = Marshal.GetFunctionPointerForDelegate(fn);
            _exports.Add("pmGetApiVersion");
        }

        public IntPtr? LoadLibrary(string[] candidatePaths, out string? failureReason)
        {
            failureReason = _failureReason;
            if (_failureReason is not null)
            {
                return null;
            }
            try
            {
                return _load(candidatePaths);
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return null;
            }
        }

        public IntPtr? GetExport(IntPtr library, string name)
        {
            if (!_exports.Contains(name))
            {
                return null;
            }
            return name == "pmGetApiVersion" && _apiVersionPointer != IntPtr.Zero
                ? _apiVersionPointer
                : (IntPtr)0x20;
        }
    }
}
