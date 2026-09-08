using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.Tests;

/// <summary>
/// WidgetRehydrator at its interface: the create/init/property/dispose
/// sequencing that ProfileOps routes its placement and import paths through.
/// Driven without a profile round-trip — a loader, a context, and a placed
/// instance are enough — so each rehydration rule (the declared-preset size
/// repair, the identity sync, the property apply, the replaced-instance
/// disposal, the per-widget failure containment) is assertable where it lives.
/// The full ImportJson round-trips stay pinned in ProfileOpsTests.
/// </summary>
[TestClass]
public class WidgetRehydratorTests
{
    [WidgetMetadata("rehydrate_test_widget", "Rehydrate Test")]
    private sealed class RehydrateTestWidget : ModernWidgetBase
    {
        [WidgetProperty("Label", WidgetPropertyType.Text, defaultValue: "default")]
        public string Label { get; set; } = "default";

        public bool Disposed { get; private set; }
        public string? LastChange { get; private set; }

        public override void Render(SKCanvas canvas, SKRect bounds) { }

        public override void OnPropertyChanged(string propertyName, object? newValue)
            => LastChange = propertyName;

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    [WidgetMetadata("yielding_init_widget", "Yielding Init")]
    private sealed class YieldingInitWidget : ModernWidgetBase
    {
        public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
        {
            // Yields from InitializeAsync: the returned ValueTask is not
            // completed at the call site (IsCompletedSuccessfully is false),
            // so synchronous rehydration must skip it instead of blocking the
            // UI thread on its continuation. The skipped-init teardown path
            // completes it off-thread; the empty body makes that a no-op.
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(tcs.Task);
        }

        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    private static WidgetPluginLoader CreateLoader(params Type[] widgets)
    {
        var loader = new WidgetPluginLoader();
        foreach (var type in widgets)
        {
            loader.RegisterBuiltInPlugin(type);
        }
        return loader;
    }

    [TestMethod]
    public void Rehydrate_OmittedSize_RepairsToTheDeclaredPreset()
    {
        var loader = CreateLoader(typeof(RehydrateTestWidget));
        var placed = new PlacedWidgetInstance { PluginId = "rehydrate_test_widget" };

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNotNull(instance, "Rehydration must succeed");
        Assert.AreEqual(406f, placed.Width, "the omitted width must fall back to the widget's declared preset");
        Assert.AreEqual(296f, placed.Height, "the omitted height must fall back to the widget's declared preset (the 2x2 house default)");
    }

    [TestMethod]
    public void Rehydrate_ExplicitSize_WinsOverTheDeclaredPreset()
    {
        var loader = CreateLoader(typeof(RehydrateTestWidget));
        var placed = new PlacedWidgetInstance { PluginId = "rehydrate_test_widget", Width = 100f, Height = 50f };

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNotNull(instance, "Rehydration must succeed");
        Assert.AreEqual(100f, placed.Width, "an explicit width must survive rehydration");
        Assert.AreEqual(50f, placed.Height, "an explicit height must survive rehydration");
    }

    [TestMethod]
    public void Rehydrate_SyncsInstanceIdAndAppliesStoredProperties()
    {
        var loader = CreateLoader(typeof(RehydrateTestWidget));
        var placed = new PlacedWidgetInstance { PluginId = "rehydrate_test_widget", InstanceId = "instance-42" };
        placed.PropertyValues["Label"] = "custom-label";

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNotNull(instance, "Rehydration must succeed");
        var widget = (RehydrateTestWidget)instance;
        Assert.AreEqual("instance-42", widget.InstanceId, "the placed InstanceId must be synced onto the fresh instance");
        Assert.AreEqual("custom-label", widget.Label, "the stored property value must be applied");
        Assert.AreEqual(nameof(RehydrateTestWidget.Label), widget.LastChange, "applying the property must raise the change");
        Assert.AreSame(widget, placed.ActiveInstance, "the new instance must become the placed's active instance");
    }

    [TestMethod]
    public void Rehydrate_DisposesTheInstanceItReplaces()
    {
        var loader = CreateLoader(typeof(RehydrateTestWidget));
        var placed = new PlacedWidgetInstance { PluginId = "rehydrate_test_widget" };
        var old = new RehydrateTestWidget();
        placed.ActiveInstance = old;

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNotNull(instance, "Rehydration must succeed");
        Assert.IsTrue(old.Disposed, "Rehydration must dispose the instance it replaces");
        Assert.AreSame((object)instance, (object)placed.ActiveInstance, "The new instance must replace the disposed one");
    }

    [TestMethod]
    public void Rehydrate_UnknownPlugin_IsLoggedAndSkipped()
    {
        var loader = CreateLoader();
        var placed = new PlacedWidgetInstance { PluginId = "no_such_plugin" };

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNull(instance, "An unknown plugin must be skipped");
        Assert.IsNull(placed.ActiveInstance, "A skipped rehydration must leave no active instance");
    }

    [TestMethod]
    public void Rehydrate_YieldingInitialize_IsSkippedWithoutBlocking()
    {
        var loader = CreateLoader(typeof(YieldingInitWidget));
        var placed = new PlacedWidgetInstance { PluginId = "yielding_init_widget" };

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNull(instance, "A widget that yields from InitializeAsync must be skipped");
        Assert.IsNull(placed.ActiveInstance, "A skipped rehydration must leave no active instance");
    }

    [TestMethod]
    public void DisposeInstance_TearsDownTheActiveInstance()
    {
        var widget = new RehydrateTestWidget();
        var placed = new PlacedWidgetInstance { PluginId = "rehydrate_test_widget", ActiveInstance = widget };

        WidgetRehydrator.DisposeInstance(placed);

        Assert.IsTrue(widget.Disposed, "DisposeInstance must dispose the active instance");
        Assert.IsNull(placed.ActiveInstance, "DisposeInstance must detach the instance from the placed record");
    }
}
