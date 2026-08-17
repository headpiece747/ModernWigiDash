using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The <see cref="PictureSourcePolicy"/> resolution table — pure, no
/// filesystem: how (source mode, path-is-a-file, path-is-a-folder) resolves to
/// the active source, when a tap promises picture cycling, and the placeholder
/// hint that follows the verdict.
/// </summary>
[TestClass]
public class PictureSourcePolicyTests
{
    private static PictureSourcePolicy.PictureSourceKind Resolve(string? mode, bool file, bool folder)
        => PictureSourcePolicy.Resolve(mode, file, folder);

    // ── Resolution table ─────────────────────────────────────────

    [TestMethod]
    public void Resolve_ForcedFolder_ResolvesFolderOnlyWhenItExists()
    {
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.Folder, Resolve(PictureSourceMode.FolderCycle, false, true));
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.None, Resolve(PictureSourceMode.FolderCycle, true, false), "a forced folder mode never reads a file path");
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.None, Resolve(PictureSourceMode.FolderCycle, false, false));
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.Folder, Resolve(PictureSourceMode.FolderCycle, true, true), "the folder wins over the file in forced mode");
    }

    [TestMethod]
    public void Resolve_ForcedSingleImage_NeverReadsTheFolder()
    {
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.File, Resolve(PictureSourceMode.SingleImage, true, true));
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.None, Resolve(PictureSourceMode.SingleImage, false, true), "a single-image widget must not fall through to the folder");
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.None, Resolve(PictureSourceMode.SingleImage, false, false));
    }

    [TestMethod]
    public void Resolve_Auto_PrefersTheFileOverTheFolder()
    {
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.File, Resolve(PictureSourceMode.Auto, true, true));
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.File, Resolve(PictureSourceMode.Auto, true, false));
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.Folder, Resolve(PictureSourceMode.Auto, false, true));
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.None, Resolve(PictureSourceMode.Auto, false, false));
    }

    [TestMethod]
    public void Resolve_UnknownMode_BehavesLikeAuto()
    {
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.Folder, Resolve("Not A Mode", false, true), "a hand-edited profile value takes the property default's rule");
        Assert.AreEqual(PictureSourcePolicy.PictureSourceKind.File, Resolve(null, true, false));
    }

    // ── CanCycle: the tap-to-cycle promise ───────────────────────

    [TestMethod]
    public void CanCycle_HoldsOnlyForAnActuallyCyclingFolder()
    {
        Assert.IsTrue(PictureSourcePolicy.CanCycle(PictureSourceMode.FolderCycle, false, true));
        Assert.IsTrue(PictureSourcePolicy.CanCycle(PictureSourceMode.Auto, false, true));
        Assert.IsFalse(PictureSourcePolicy.CanCycle(PictureSourceMode.SingleImage, true, true), "a single-image widget must not promise a cycle");
        Assert.IsFalse(PictureSourcePolicy.CanCycle(PictureSourceMode.FolderCycle, false, false), "a folder that does not exist cannot cycle — the hint must not promise it");
        Assert.IsFalse(PictureSourcePolicy.CanCycle(PictureSourceMode.Auto, true, false), "Auto on a file path shows one picture, not a cycle");
    }

    [TestMethod]
    public void PlaceholderHint_FollowsTheCycleVerdict()
    {
        Assert.AreEqual("Click/Tap to Cycle Pictures", PictureSourcePolicy.PlaceholderHint(true));
        Assert.AreEqual("Tap to set an Image Path", PictureSourcePolicy.PlaceholderHint(false));
    }
}
