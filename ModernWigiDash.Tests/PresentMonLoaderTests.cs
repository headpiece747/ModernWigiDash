using System.IO;
using System.Runtime.InteropServices;
using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

/// <summary>
/// The PresentMon library-search policy: the candidate-path order (pinned so
/// an install-layout change fails loudly) and the loader's
/// empty/missing-candidate handling (no native calls, no load failure).
/// </summary>
[TestClass]
public class PresentMonLoaderTests
{
    [TestMethod]
    public void PresentMonLibraryCandidates_SharedServiceBeforeSdk_ProgramFilesBeforeX86()
    {
        string[] expected =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMonSharedService", "PresentMonAPI2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMon", "SDK", "PresentMonAPI2.dll"),
        ];

        CollectionAssert.AreEqual(expected, PresentMonApiProbe.PresentMonLibraryCandidates(),
            "the MSI's shared-service layout must be probed before the SDK layout, x64 before x86");
    }

    [TestMethod]
    public void LoadLibrary_EmptyCandidateList_ReturnsNullWithoutReason()
    {
        IntPtr? handle = NativePresentMonLibraryLoader.Instance.LoadLibrary([], out string? failureReason);

        Assert.IsNull(handle);
        Assert.IsNull(failureReason, "no candidates is not a load failure — the probe reports the not-found reason");
    }

    [TestMethod]
    public void LoadLibrary_OnlyMissingPaths_ReturnsNullWithoutReason()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "PresentMonAPI2.dll");

        IntPtr? handle = NativePresentMonLibraryLoader.Instance.LoadLibrary([missing], out string? failureReason);

        Assert.IsNull(handle);
        Assert.IsNull(failureReason, "a missing file is skipped silently, not read as a load failure");
    }
}
