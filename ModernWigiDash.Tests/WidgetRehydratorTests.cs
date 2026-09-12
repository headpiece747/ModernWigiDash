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

    /// <summary>A widget whose constructor throws: the pin for the rehydration
    /// Broken-result leg. Reached only through reflection (the loader
    /// instantiates it); S1144 is suppressed because the analyzer cannot see
    /// the reflection-only reach of the throwing ctor.</summary>
#pragma warning disable S1144 // ctor is reached only via reflection (the loader instantiates it)
    [WidgetMetadata("broken_ctor_widget", "Broken Ctor")]
    private sealed class BrokenCtorWidget : ModernWidgetBase
    {
        public BrokenCtorWidget() => throw new InvalidOperationException("ctor boom");
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }
#pragma warning restore S1144

    /// <summary>A widget whose property setter throws on any assignment: the
    /// pin for the property-apply catch leg (a stored value that the setter
    /// rejects is logged and ignored, not fatal to rehydration).</summary>
    [WidgetMetadata("strict_prop_widget", "Strict Prop")]
    private sealed class StrictPropWidget : ModernWidgetBase
    {
        // The setter throws on every assignment so rehydration's property-apply
        // catch leg is driven deterministically; the getter returns a constant
        // (no backing field, so there is nothing unassigned to warn about).
        [WidgetProperty("Count", WidgetPropertyType.Number, defaultValue: "0")]
        public int Count
        {
            get => 0;
            set => throw new InvalidOperationException("setter rejects every value");
        }

        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    [TestMethod]
    public void Rehydrate_BrokenPlugin_IsLoggedAndSkipped()
    {
        // A plugin whose constructor throws is a Broken create result: the
        // rehydration must log the break and skip it (distinct from NotFound),
        // leaving no active instance.
        var loader = CreateLoader(typeof(BrokenCtorWidget));
        var placed = new PlacedWidgetInstance { PluginId = "broken_ctor_widget" };

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNull(instance, "A broken plugin must be skipped");
        Assert.IsNull(placed.ActiveInstance, "A skipped rehydration must leave no active instance");
    }

    [TestMethod]
    public void Rehydrate_SetterRejectingStoredValue_IsIgnoredNotFatal()
    {
        // A stored property value that the property's setter rejects (throws on
        // assignment) must hit the property-apply catch: it is logged and
        // ignored, and rehydration still succeeds (the rest of the profile
        // loads). The StrictPropWidget's Count setter throws on every set.
        var loader = CreateLoader(typeof(StrictPropWidget));
        var placed = new PlacedWidgetInstance { PluginId = "strict_prop_widget" };
        placed.PropertyValues["Count"] = 42; // converts to int; the setter then throws

        IModernWidget? instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNotNull(instance, "Rehydration must succeed despite a rejected stored value");
        Assert.AreSame(instance, placed.ActiveInstance, "the instance must still be installed");
    }

    /// <summary>A widget whose InitializeAsync throws synchronously: the pin for
    /// the outer rehydration catch leg (a fault after the instance is created
    /// must log, dispose the half-initialized instance, and skip the widget
    /// without breaking the rest of the profile).</summary>
    [WidgetMetadata("throwing_init_widget", "Throwing Init")]
    private sealed class ThrowingInitWidget : ModernWidgetBase
    {
        public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("init boom");

        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    [TestMethod]
    public void Rehydrate_ThrowingInitialize_IsLoggedAndSkippedWithDisposal()
    {
        // A widget whose InitializeAsync throws (synchronously, not yielding)
        // must hit the outer catch: it logs the failure, disposes the
        // half-initialized instance, and skips the widget (returns null),
        // leaving no active instance.
        var loader = CreateLoader(typeof(ThrowingInitWidget));
        var placed = new PlacedWidgetInstance { PluginId = "throwing_init_widget" };

        var instance = WidgetRehydrator.Rehydrate(loader, new TestContext(), placed);

        Assert.IsNull(instance, "A throwing init must be skipped");
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
