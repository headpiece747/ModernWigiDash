using System.IO;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// The manual profile export flow pinned at the module's interface (no window):
/// the success path composes the bundle (the profile's bare JSON plus the active
/// theme as the ADR-0021 section) and surfaces the success line, and a write
/// failure surfaces the error line with the exception message. The sequence is
/// driven through the in-memory fake host (the ADR-0008 image).
/// </summary>
[TestClass]
public class ProfileExportFlowTests
{
    private sealed class FakeHost : IProfileExportHost
    {
        public ProfileLayout Profile { get; set; } = new();
        public ThemeSettings CurrentTheme { get; set; } = new ThemeSettings();
        public List<(string Title, string Message)> Successes { get; } = [];
        public List<(string Title, string Message)> Errors { get; } = [];

        public void ShowSuccess(string title, string message) => Successes.Add((title, message));

        public void ShowError(string title, string message) => Errors.Add((title, message));
    }

    [TestMethod]
    public void Run_WriteSucceeds_ExportsTheBundleAndSurfacesSuccess()
    {
        var host = new FakeHost();
        string path = Path.Combine(Path.GetTempPath(), $"wmd-export-{Guid.NewGuid():N}.json");
        try
        {
            var outcome = ProfileExportFlow.Run(path, host);

            Assert.AreEqual(new ProfileExportFlowOutcome.Exported(), outcome);
            Assert.IsTrue(File.Exists(path), "the bundle was written to the chosen path");
            string json = File.ReadAllText(path);
            // The bundle carries both the profile's root fields and the theme
            // section (ADR-0021: the theme rides the export bundle).
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.IsTrue(doc.RootElement.TryGetProperty("Pages", out _), "the profile's own fields stay at the root");
            Assert.IsTrue(doc.RootElement.TryGetProperty("theme", out _), "the theme section rides the bundle");
            Assert.AreEqual(1, host.Successes.Count, "the success line was surfaced exactly once");
            Assert.AreEqual("Export Complete", host.Successes[0].Title);
            Assert.AreEqual(0, host.Errors.Count, "a successful write surfaces no error line");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Run_WriteFails_SurfacesTheErrorLineWithTheMessage()
    {
        // A path whose parent directory does not exist makes the write throw;
        // the flow catches it and surfaces the error line instead of throwing.
        var host = new FakeHost();
        string badPath = Path.Combine(Path.GetTempPath(), "does-not-exist-wmd", $"{Guid.NewGuid():N}.json");

        var outcome = ProfileExportFlow.Run(badPath, host);

        Assert.IsInstanceOfType(outcome, typeof(ProfileExportFlowOutcome.Failed));
        Assert.AreEqual(1, host.Errors.Count, "a failed write surfaces exactly one error line");
        Assert.AreEqual("Export Error", host.Errors[0].Title);
        Assert.IsFalse(string.IsNullOrEmpty(host.Errors[0].Message), "the error line carries the exception message");
        Assert.AreEqual(0, host.Successes.Count, "a failed write surfaces no success line");
    }
}
