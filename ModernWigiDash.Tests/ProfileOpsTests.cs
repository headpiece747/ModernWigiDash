using System.Net;
using System.Net.Http;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class ProfileOpsTests
{

    [WidgetMetadata("profile_test_widget", "Profile Test")]
    private sealed class TestWidget : ModernWidgetBase
    {
        [WidgetProperty("Label", WidgetPropertyType.Text, defaultValue: "default")]
        public string Label { get; set; } = "default";

        public override SKSize DefaultSize => new(406, 148);
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    /// <summary>A widget that toggles its own property via SetProperty on touch — the
    /// WeatherForecastWidget OnTouch shape. The property is [WidgetProperty] with
    /// a public setter, exactly like rehydration requires.</summary>
    [WidgetMetadata("toggle_test_widget", "Toggle Test")]
    private sealed class ToggleWidget : ModernWidgetBase
    {
        [WidgetProperty("Mode", WidgetPropertyType.Text, defaultValue: "A")]
        public string Mode { get; set; } = "A";

        public override void Render(SKCanvas canvas, SKRect bounds) { }

        public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
            => SetProperty(nameof(Mode), Mode == "A" ? "B" : "A");
    }

    [WidgetMetadata("fullscreen_test_widget", "Fullscreen Test")]
    private sealed class FullScreenTestWidget : ModernWidgetBase
    {
        public override SKSize DefaultSize => new(DisplayGeometry.FramebufferWidth, DisplayGeometry.FramebufferHeight);
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    [WidgetMetadata("disposable_test_widget", "Disposable Test")]
    private sealed class DisposableTestWidget : ModernWidgetBase
    {
        public bool Disposed { get; private set; }

        public override SKSize DefaultSize => new(406, 148);
        public override void Render(SKCanvas canvas, SKRect bounds) { }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    private static WidgetPluginLoader CreateLoader()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(TestWidget));
        return loader;
    }


    private static ProfileLayout CreateProfile(WidgetPluginLoader loader, TestContext context)
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 10, 20, 406, 148);
        placed!.PropertyValues["Label"] = "custom";
        return profile;
    }

    // ── page CRUD ───────────────────────────────────────────

    [TestMethod]
    public void AddPage_AddsAndActivates()
    {
        // ProfileLayout starts with one default page.
        var profile = new ProfileLayout();
        var page = ProfileOps.AddPage(profile, "First");

        Assert.AreEqual(2, profile.Pages.Count);
        Assert.AreEqual("First", page.PageName);
        Assert.AreEqual(1, profile.ActivePageIndex);

        ProfileOps.AddPage(profile);
        Assert.AreEqual(3, profile.Pages.Count);
        Assert.AreEqual("Page 3", profile.Pages[2].PageName, "Unnamed adds number from the full page count");
        Assert.AreEqual(2, profile.ActivePageIndex);
    }

    [TestMethod]
    public void DeletePage_LastPage_IsRefused()
    {
        var profile = new ProfileLayout();

        Assert.IsFalse(ProfileOps.DeletePage(profile, 0));
        Assert.AreEqual(1, profile.Pages.Count);
    }

    [TestMethod]
    public void DeletePage_RemovesAndClampsActiveIndex()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "A");
        ProfileOps.AddPage(profile, "B");
        ProfileOps.AddPage(profile, "C");
        profile.ActivePageIndex = 3;

        Assert.IsTrue(ProfileOps.DeletePage(profile, 3));
        Assert.AreEqual(3, profile.Pages.Count);
        Assert.AreEqual(2, profile.ActivePageIndex, "Active index must clamp after deleting the last page");
    }

    [TestMethod]
    public void SetActivePageIndex_ValidIndex_Switches()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "A");

        Assert.IsTrue(ProfileOps.SetActivePageIndex(profile, 1));
        Assert.AreEqual(1, profile.ActivePageIndex);
    }

    [TestMethod]
    public void SetActivePageIndex_OutOfRange_IsRefused()
    {
        var profile = new ProfileLayout();

        Assert.IsFalse(ProfileOps.SetActivePageIndex(profile, -1));
        Assert.IsFalse(ProfileOps.SetActivePageIndex(profile, profile.Pages.Count));
        Assert.AreEqual(0, profile.ActivePageIndex, "A refused switch must leave the active page untouched");
    }

    [TestMethod]
    public void ActivePageIndex_ClampsToPageRange()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "A");
        ProfileOps.AddPage(profile, "B");

        profile.ActivePageIndex = 99;
        Assert.AreEqual(2, profile.ActivePageIndex, "The index must clamp to the last page, never past it");
        Assert.AreEqual(profile.Pages[2], profile.ActivePage, "ActivePage must resolve to a page that is part of the profile");
    }

    [TestMethod]
    public void ImportJson_EmptyPagesArray_IsRepairedWithOnePage()
    {
        // A profile with zero pages would hand ActivePage an orphan page not
        // in Pages — the sanitizer must repair the import.
        var loaded = ProfileOps.ImportJson("""{"profileId":"x","pages":[]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages.Count, "The sanitizer must guarantee at least one page");
        Assert.AreEqual(loaded.Pages[0], loaded.ActivePage, "ActivePage must never be an orphan");
    }

    [TestMethod]
    public void ImportJson_NullPagesCollection_IsRepaired()
    {
        // Regression guard: untrusted JSON with "pages": null NRE'd in the
        // sanitizer (the guard crashed on exactly the input it exists for).
        var loaded = ProfileOps.ImportJson("""{"profileId":"x","pages":null}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages.Count);
    }

    [TestMethod]
    public void ImportJson_NullWidgetsCollection_IsRepaired()
    {
        var loaded = ProfileOps.ImportJson("""{"profileId":"x","pages":[{"pageName":"A","widgets":null}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(0, loaded.ActivePage.Widgets.Count, "A null widgets array must import as an empty page");
    }

    [TestMethod]
    public void ImportJson_NullPropertyValuesCollection_IsRepaired()
    {
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"profile_test_widget","PropertyValues":null}]}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.ActivePage.Widgets.Count, "A widget with null propertyValues must still import");
    }

    // ── widget-property bookkeeping: SetProperty → PropertyValues → export ──

    [TestMethod]
    public void SetProperty_WritesInstanceAndPropertyValues()
    {
        var profile = new ProfileLayout();
        var context = new PersistingContext(profile);
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(ToggleWidget));
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "toggle_test_widget", 0, 0, 406, 148)!;
        var widget = (ToggleWidget)placed.ActiveInstance!;
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

        widget.OnTouch(default, TouchEventType.TouchUp);

        Assert.AreEqual("B", widget.Mode, "SetProperty must update the instance");
        Assert.AreEqual("B", placed.PropertyValues["Mode"], "SetProperty must persist the companion PropertyValues entry");
    }

    [TestMethod]
    public void SetProperty_Toggle_SurvivesExportImport()
    {
        var profile = new ProfileLayout();
        var context = new PersistingContext(profile);
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(ToggleWidget));
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "toggle_test_widget", 0, 0, 406, 148)!;
        var widget = (ToggleWidget)placed.ActiveInstance!;
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        widget.OnTouch(default, TouchEventType.TouchUp);

        string json = ProfileOps.ExportJson(profile);
        var reloaded = ProfileOps.ImportJson(json, loader, new TestContext());

        var reloadedWidget = reloaded!.ActivePage.Widgets.Single().ActiveInstance as ToggleWidget;
        Assert.IsNotNull(reloadedWidget);
        Assert.AreEqual("B", reloadedWidget.Mode, "A SetProperty toggle must survive Export→Import");
    }

    [TestMethod]
    public void WeatherWidget_OnTouchUnitToggle_SurvivesExportImport()
    {
        // Regression guard for the confirmed bug: WeatherForecastWidget.OnTouch
        // mutated UnitSystem/LayoutMode without writing PropertyValues, so the
        // toggles silently vanished on Export→Import.
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(WeatherForecastWidget));
        var profile = new ProfileLayout();
        var context = new PersistingContext(profile);
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "weather_forecast", 0, 0, 1016, 592)!;
        var widget = (WeatherForecastWidget)placed.ActiveInstance!;
        widget.TestHttpClient = new HttpClient(new StubHttpHandler("{}"));
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

        string initial = widget.UnitSystem;
        widget.OnTouch(new SKPoint(980, 20), TouchEventType.TouchUp); // unit toggle zone (top-right)

        Assert.AreNotEqual(initial, widget.UnitSystem, "The touch toggle must switch the unit system");
        Assert.IsTrue(placed.PropertyValues.ContainsKey(nameof(WeatherForecastWidget.UnitSystem)),
            "The OnTouch toggle must persist to PropertyValues");

        string json = ProfileOps.ExportJson(profile);
        var reloaded = ProfileOps.ImportJson(json, loader, new TestContext());

        var reloadedWidget = reloaded!.ActivePage.Widgets.Single().ActiveInstance as WeatherForecastWidget;
        Assert.IsNotNull(reloadedWidget);
        Assert.AreEqual(widget.UnitSystem, reloadedWidget.UnitSystem, "The unit toggle must survive Export→Import");
    }

    [TestMethod]
    public void PlaceCentered_FullScreenWidget_GoesToOrigin()
    {
        var profile = new ProfileLayout();
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(FullScreenTestWidget));

        var placed = ProfileOps.PlaceCentered(profile, loader, new TestContext(), "fullscreen_test_widget");

        Assert.IsNotNull(placed);
        Assert.AreEqual(0f, placed.X);
        Assert.AreEqual(0f, placed.Y);
        Assert.AreEqual(DisplayGeometry.FramebufferWidth, placed.Width);
        Assert.AreEqual(DisplayGeometry.FramebufferHeight, placed.Height);
    }

    [TestMethod]
    public void PlaceCentered_SmallWidget_CentersOnGrid()
    {
        var profile = new ProfileLayout();
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(TestWidget)); // 406 x 148

        var placed = ProfileOps.PlaceCentered(profile, loader, new TestContext(), "profile_test_widget");

        Assert.IsNotNull(placed);
        float cx = (float)Math.Round(DisplayGeometry.FramebufferWidth / 2.0 / GridSizeExtensions.CellWidth) * GridSizeExtensions.CellWidth;
        float cy = (float)Math.Round(DisplayGeometry.FramebufferHeight / 2.0 / GridSizeExtensions.CellHeight) * GridSizeExtensions.CellHeight;
        Assert.AreEqual(cx - 406f / 2, placed.X);
        Assert.AreEqual(cy - 148f / 2, placed.Y);
    }


    [TestMethod]
    public void RenamePage_BlankName_IsIgnored()
    {
        var profile = new ProfileLayout();
        var page = ProfileOps.AddPage(profile, "A");

        ProfileOps.RenamePage(page, "   ");

        Assert.AreEqual("A", page.PageName);
        ProfileOps.RenamePage(page, "B");
        Assert.AreEqual("B", page.PageName);
    }

    [TestMethod]
    public void ClearPage_EmptiesWidgets()
    {
        var profile = CreateProfile(CreateLoader(), new TestContext());

        ProfileOps.ClearPage(profile.ActivePage);

        Assert.AreEqual(0, profile.ActivePage.Widgets.Count);
    }

    // ── placement ───────────────────────────────────────────

    [TestMethod]
    public void PlaceWidget_UnknownPlugin_ReturnsNull()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile);
        var loader = CreateLoader();

        var placed = ProfileOps.PlaceWidget(profile, loader, new TestContext(), "no_such_plugin", 0, 0);

        Assert.IsNull(placed);
        Assert.AreEqual(0, profile.ActivePage.Widgets.Count);
    }

    [TestMethod]
    public void PlaceWidget_AssignsSizeZIndexAndInstance()
    {
        var profile = CreateProfile(CreateLoader(), new TestContext());

        var placed = profile.ActivePage.Widgets.Single();

        Assert.AreEqual("Profile Test", placed.DisplayName);
        Assert.AreEqual(406f, placed.Width);
        Assert.AreEqual(148f, placed.Height);
        Assert.AreEqual(1, placed.ZIndex);
        Assert.IsNotNull(placed.ActiveInstance);
    }

    // ── disposal ────────────────────────────────────────────

    [TestMethod]
    public void RehydrateWidget_DisposesReplacedInstance()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(DisposableTestWidget));
        var placed = new PlacedWidgetInstance { PluginId = "disposable_test_widget" };
        var old = new DisposableTestWidget();
        placed.ActiveInstance = old;

        var instance = ProfileOps.RehydrateWidget(loader, new TestContext(), placed);

        Assert.IsNotNull(instance, "Rehydration must succeed");
        Assert.IsTrue(old.Disposed, "Rehydration must dispose the instance it replaces");
        Assert.AreSame(instance, placed.ActiveInstance, "The new instance must replace the disposed one");
    }

    [TestMethod]
    public void ClearPage_DisposesWidgetInstances()
    {
        var page = new PageLayout();
        var first = new DisposableTestWidget();
        var second = new DisposableTestWidget();
        page.Widgets.Add(new PlacedWidgetInstance { PluginId = "a", ActiveInstance = first });
        page.Widgets.Add(new PlacedWidgetInstance { PluginId = "b", ActiveInstance = second });

        ProfileOps.ClearPage(page);

        Assert.AreEqual(0, page.Widgets.Count);
        Assert.IsTrue(first.Disposed, "ClearPage must dispose every widget instance");
        Assert.IsTrue(second.Disposed, "ClearPage must dispose every widget instance");
    }

    [TestMethod]
    public void DisposeProfile_DisposesAllInstances()
    {
        var profile = new ProfileLayout();
        var firstPage = profile.ActivePage;
        var secondPage = ProfileOps.AddPage(profile, "Second");
        var first = new DisposableTestWidget();
        var second = new DisposableTestWidget();
        firstPage.Widgets.Add(new PlacedWidgetInstance { PluginId = "a", ActiveInstance = first });
        secondPage.Widgets.Add(new PlacedWidgetInstance { PluginId = "b", ActiveInstance = second });

        ProfileOps.DisposeProfile(profile);

        Assert.IsTrue(first.Disposed, "DisposeProfile must dispose instances on every page");
        Assert.IsTrue(second.Disposed, "DisposeProfile must dispose instances on every page");
        Assert.AreEqual(2, profile.Pages.Count, "Page structure is left intact");
        Assert.AreEqual(1, profile.ActivePage.Widgets.Count, "Widgets stay in place; only instances are disposed");
    }

    [TestMethod]
    public void RemoveWidget_RemovesFromPageAndDisposes()
    {
        var page = new PageLayout();
        var widget = new DisposableTestWidget();
        var placed = new PlacedWidgetInstance { PluginId = "a", ActiveInstance = widget };
        page.Widgets.Add(placed);

        var removed = ProfileOps.RemoveWidget(page, placed);

        Assert.IsTrue(removed, "The widget on the page must be reported as removed");
        Assert.AreEqual(0, page.Widgets.Count);
        Assert.IsTrue(widget.Disposed, "RemoveWidget must dispose the active instance");
    }

    [TestMethod]
    public void RemoveWidget_WidgetNotOnPage_ReturnsFalseAndDoesNotDispose()
    {
        var page = new PageLayout();
        var widget = new DisposableTestWidget();
        var placed = new PlacedWidgetInstance { PluginId = "a", ActiveInstance = widget };

        var removed = ProfileOps.RemoveWidget(page, placed);

        Assert.IsFalse(removed, "A widget absent from the page must not be reported as removed");
        Assert.IsFalse(widget.Disposed, "A widget we never owned must not be disposed");
        Assert.AreEqual(0, page.Widgets.Count);
    }

    // ── export / import round-trip ──────────────────────────

    [TestMethod]
    public void ExportImport_RoundTripsPagesPlacementsAndPropertyValues()
    {
        var loader = CreateLoader();
        var context = new TestContext();
        var profile = CreateProfile(loader, context);
        ProfileOps.AddPage(profile, "Second");
        var second = ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 100, 200, 203, 148);
        second!.PropertyValues["Label"] = "second page label";

        string json = ProfileOps.ExportJson(profile);
        var loaded = ProfileOps.ImportJson(json, loader, context);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(3, loaded.Pages.Count, "Default page + Main + Second");
        Assert.AreEqual("Main", loaded.Pages[1].PageName);
        Assert.AreEqual("Second", loaded.Pages[2].PageName);
        Assert.AreEqual(1, loaded.Pages[1].Widgets.Count);
        Assert.AreEqual(1, loaded.Pages[2].Widgets.Count);

        var first = loaded.Pages[1].Widgets[0];
        Assert.AreEqual(10f, first.X);
        Assert.AreEqual(20f, first.Y);
        Assert.IsTrue(first.PropertyValues.ContainsKey("Label"), "Imported PropertyValues survive the round-trip");
        Assert.IsNotNull(first.ActiveInstance, "Imported widgets must be rehydrated");

        var rehydrated = (TestWidget)first.ActiveInstance;
        Assert.AreEqual("custom", rehydrated.Label, "Rehydration must apply custom property values");
        var secondInstance = (TestWidget)loaded.Pages[2].Widgets[0].ActiveInstance!;
        Assert.AreEqual("second page label", secondInstance.Label);
    }

    [TestMethod]
    public void ImportJson_InvalidJson_ReturnsNull()
    {
        var loader = CreateLoader();

        Assert.IsNull(ProfileOps.ImportJson("{not json", loader, new TestContext()));
        Assert.IsNull(ProfileOps.ImportJson("null", loader, new TestContext()));
    }

    // ── untrusted-import sanitization ───────────────────────

    [TestMethod]
    public void ImportJson_UntrustedProfile_ClearsActionCommandsAndRootedPaths()
    {
        var loader = CreateLoader();
        var context = new TestContext();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 0, 0);
        placed!.PropertyValues["ActionType"] = "Launch App";
        placed.PropertyValues["ActionCommand"] = @"C:\Windows\System32\calc.exe";
        placed.PropertyValues["IconFile"] = @"C:\absolute\icon.svg";
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, context);

        var imported = loaded!.Pages[1].Widgets[0];
        Assert.AreEqual("", imported.PropertyValues["ActionCommand"], "Action commands must be cleared on import");
        Assert.AreEqual("", imported.PropertyValues["IconFile"], "Rooted icon paths must be cleared on import");
    }

    [TestMethod]
    public void ImportJson_ActionCommandWithoutActionType_IsStillCleared()
    {
        // Regression: the old guard required BOTH ActionCommand and ActionType
        // to be present. ActionType has a default, so a crafted profile with
        // only ActionCommand slipped through to command execution.
        var loader = CreateLoader();
        var context = new TestContext();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 0, 0);
        placed!.PropertyValues["ActionCommand"] = "calc.exe";
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, context);

        var imported = loaded!.Pages[1].Widgets[0];
        Assert.AreEqual("", imported.PropertyValues["ActionCommand"], "ActionCommand must be cleared even without ActionType");
    }

    [TestMethod]
    public void ImportJson_ExcessiveWidgetCount_IsCapped()
    {
        var loader = CreateLoader();
        var context = new TestContext();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        for (int i = 0; i < 300; i++)
        {
            ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 0, 0);
        }
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, context);

        // Total cap (1000) exceeds the per-page cap (200): the page must be
        // trimmed to the per-page limit, keeping the import bounded.
        Assert.IsTrue(loaded!.Pages[1].Widgets.Count <= 200, "Page widget count must be capped on import");
    }

    [TestMethod]
    public void ImportJson_PathTraversal_IsCleared()
    {
        var loader = CreateLoader();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        var placed = ProfileOps.PlaceWidget(profile, loader, new TestContext(), "profile_test_widget", 0, 0);
        placed!.PropertyValues["ImagePath"] = @"..\..\secret.png";
        placed.PropertyValues["IconFile"] = @"\\server\share\evil.svg";
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, new TestContext());

        var imported = loaded!.Pages[1].Widgets[0];
        Assert.AreEqual("", imported.PropertyValues["ImagePath"], "Traversal paths must be cleared");
        Assert.AreEqual("", imported.PropertyValues["IconFile"], "UNC paths must be cleared");
    }

    [TestMethod]
    public void ImportJson_OversizedPage_IsCapped()
    {
        var loader = CreateLoader();
        var context = new TestContext();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        for (int i = 0; i < 250; i++)
        {
            ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 0, 0);
        }
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, context);

        Assert.AreEqual(200, loaded!.Pages[1].Widgets.Count, "Imported pages must be capped to the widget limit");
    }

    [TestMethod]
    public void ImportJson_OwnProfile_KeepsSafeRelativePaths()
    {
        var loader = CreateLoader();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        var placed = ProfileOps.PlaceWidget(profile, loader, new TestContext(), "profile_test_widget", 0, 0);
        placed!.PropertyValues["IconFile"] = "icons/my-icon.svg";
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, new TestContext());

        Assert.AreEqual("icons/my-icon.svg", loaded!.Pages[1].Widgets[0].PropertyValues["IconFile"]);
    }

    [TestMethod]
    public void ConvertPropertyValue_JsonElement_DeserializesToTargetType()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("42");
        var element = doc.RootElement;

        Assert.AreEqual(42, ProfileOps.ConvertPropertyValue(element, typeof(int)));
        Assert.IsNull(ProfileOps.ConvertPropertyValue(element, typeof(DateTime)), "Incompatible conversion must return null");
    }
}
