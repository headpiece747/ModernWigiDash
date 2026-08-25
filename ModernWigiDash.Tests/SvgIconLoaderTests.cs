using System.IO;

namespace ModernWigiDash.Tests;

/// <summary>
/// The icon-probe cache: positive AND negative existence results are cached
/// per path (a missing IconFile must not hit File.Exists 30×/s), and
/// CopyToIcons — the one runtime path that adds icon files — makes its
/// destination resolvable on the next probe.
/// </summary>
[TestClass]
public class SvgIconLoaderTests
{
    private const string SinglePathSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16v16H4z\"/></svg>";

    private static string WriteTempSvg(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wmd_svg_{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void TryGetPath_MissingFile_CachesNegative()
    {
        string iconPath = WriteTempSvg("placeholder");
        File.Delete(iconPath);

        // First probe: the path is missing — the negative result is cached.
        Assert.IsFalse(SvgIconLoader.TryGetPath(iconPath, out _));

        // A file appearing at the same path does NOT flip the cached negative:
        // the runtime copy flow (CopyToIcons) is the only invalidation path.
        File.WriteAllText(iconPath, SinglePathSvg);
        try
        {
            Assert.IsFalse(SvgIconLoader.TryGetPath(iconPath, out _), "A cached negative must not re-probe the filesystem");
        }
        finally
        {
            File.Delete(iconPath);
        }
    }

    [TestMethod]
    public void TryGetPath_ExistingFile_IsCachedPositive()
    {
        string iconPath = WriteTempSvg(SinglePathSvg);

        Assert.IsTrue(SvgIconLoader.TryGetPath(iconPath, out var path));
        Assert.IsNotNull(path);
        Assert.IsFalse(path.IsEmpty);

        // The positive result is cached: the probe still resolves after the
        // file is gone (both the existence entry and the parsed path persist).
        File.Delete(iconPath);
        Assert.IsTrue(SvgIconLoader.TryGetPath(iconPath, out _), "A cached positive must not re-probe the filesystem");
    }

    [TestMethod]
    public void CopyToIcons_MakesTheCopiedIconResolvable()
    {
        string sourcePath = WriteTempSvg(SinglePathSvg);
        string copiedPath = "";
        try
        {
            string fileName = SvgIconLoader.CopyToIcons(sourcePath);
            copiedPath = Path.Combine(SvgIconLoader.IconsDirectory, fileName);

            Assert.IsTrue(File.Exists(copiedPath), "CopyToIcons must copy the file into the icons directory");
            Assert.IsTrue(SvgIconLoader.TryGetPath(fileName, out var path), "The copied icon must resolve by its relative name");
            Assert.IsNotNull(path);
            Assert.IsFalse(path.IsEmpty);
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (copiedPath.Length > 0 && File.Exists(copiedPath)) File.Delete(copiedPath);
        }
    }

    [TestMethod]
    public void TryGetPath_MalformedSvg_DegradesToNoIcon_WithoutThrowing()
    {
        // A file that passes the existence check but is not valid XML must
        // degrade to a no-icon (false), not throw into the render tick that
        // probes the icon geometry every frame.
        string iconPath = WriteTempSvg("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M4 4");
        try
        {
            Assert.IsFalse(SvgIconLoader.TryGetPath(iconPath, out var path), "a malformed SVG is a no-icon, not a throw");
            Assert.IsTrue(path is null || path.IsEmpty, "a malformed SVG yields no drawable path");
        }
        finally
        {
            File.Delete(iconPath);
        }
    }
}
