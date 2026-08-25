using System.IO;
using System.Reflection;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the single "set a property value on a placed widget" commit owner:
/// the context's SetWidgetProperty (instance set + change fire + placed
/// persistence in one spelling) and the base's SetProperty routing through
/// it, including the pre-initialization leg (no context handed yet: the
/// instance still carries the value).
/// </summary>
[TestClass]
public class ModernWidgetBaseTests
{
    private static readonly PropertyInfo LabelProp = typeof(TestWidget).GetProperty(nameof(TestWidget.Label))!;

    [TestMethod]
    public async Task SetWidgetProperty_CommitsInstanceAndFiresAndPersists()
    {
        // The commit owner is a default interface member: reachable through
        // the interface reference, not the concrete test host.
        var testContext = new TestContext();
        IModernWigiDashContext context = testContext;
        var widget = new TestWidget();
        await widget.InitializeAsync(context);

        context.SetWidgetProperty(widget, LabelProp, "committed");

        Assert.AreEqual("committed", widget.Label);
        Assert.AreEqual(1, testContext.Renders, "the commit must raise the widget's change (the base default requests a repaint)");
    }

    [TestMethod]
    public async Task SetProperty_WithPersistingContext_CommitsThroughTheOwnerIntoPlacedPropertyValues()
    {
        var widget = new TestWidget();
        var placed = new PlacedWidgetInstance
        {
            PluginId = "test",
            DisplayName = "Test Widget",
            ActiveInstance = widget
        };
        var profile = new ProfileLayout();
        profile.Pages[0].Widgets.Add(placed);
        var context = new PersistingContext(profile);
        await widget.InitializeAsync(context);

        widget.SetPropertyForTest(nameof(TestWidget.Label), "via-base");

        Assert.AreEqual("via-base", widget.Label);
        Assert.AreEqual("via-base", placed.PropertyValues[nameof(TestWidget.Label)],
            "the base write path must commit through the context's commit owner into the placed instance");
    }

    [TestMethod]
    public void SetProperty_PreInitialization_SetsTheInstanceWithoutAPlacedPersist()
    {
        // No InitializeAsync: the context was never handed. The instance must
        // still carry the value (a pre-init widget's OnTouch toggle) and the
        // commit must not throw reaching for a missing placed instance.
        var widget = new TestWidget();

        widget.SetPropertyForTest(nameof(TestWidget.Label), "pre-init");

        Assert.AreEqual("pre-init", widget.Label);
    }

    [TestMethod]
    public async Task SetProperty_MissingProperty_WritesNothingAndRaisesNoChange()
    {
        var context = new TestContext();
        var widget = new TestWidget();
        await widget.InitializeAsync(context);

        widget.SetPropertyForTest("NoSuchProperty", "x");

        Assert.AreEqual("seed", widget.Label);
        Assert.AreEqual(0, context.Renders, "a missing property must not raise a change");
    }

    [TestMethod]
    public void SetProperty_MissingProperty_LogsTheMissOnce_NotPerCall()
    {
        // A fresh property name: the miss is cached per (type, name), so a
        // name another test already missed would be a cache hit, not a miss.
        string logPath = Path.Combine(Path.GetTempPath(), "wmd-misslog-" + Guid.NewGuid().ToString("N") + ".log");
        FileLog.LogPath = logPath;
        try
        {
            var widget = new TestWidget();
            widget.SetPropertyForTest("NoSuchPropertyTwice", "a");
            widget.SetPropertyForTest("NoSuchPropertyTwice", "b");
            FileLog.Flush();
            // Restore the default path BEFORE reading: the FileLog writer
            // holds the temp file open (and the restore is the seam that
            // closes it), so the read has to happen on the closed file.
            FileLog.LogPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");

            int missLines = File.ReadLines(logPath).Count(line => line.Contains("NoSuchPropertyTwice"));
            Assert.AreEqual(1, missLines,
                "the miss is diagnosed once and cached (sentinel), not re-logged per call");
        }
        finally
        {
            FileLog.LogPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");
            try
            {
                File.Delete(logPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp log.
            }
        }
    }
}
