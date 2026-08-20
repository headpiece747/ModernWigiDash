using System.Net.Http;
using System.Text.Json;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class ProfileOpsTests
{

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

    [WidgetMetadata("fullscreen_test_widget", "Fullscreen Test", DefaultGridSize = GridSizePreset.Size5x4)]
    private sealed class FullScreenTestWidget : ModernWidgetBase
    {
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
    public void CanDeletePage_SinglePage_False()
    {
        var profile = new ProfileLayout();

        Assert.IsFalse(ProfileOps.CanDeletePage(profile));
    }

    [TestMethod]
    public void CanDeletePage_MultiplePages_True()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "B");

        Assert.IsTrue(ProfileOps.CanDeletePage(profile));
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

    [TestMethod]
    public void ImportJson_OverPageCap_ClampsActivePageIndex()
    {
        // A 60-page import truncates to MaxPagesPerProfile (50). The active
        // index was clamped against the ORIGINAL count during deserialization
        // (pages are listed first), so it must be re-clamped after the
        // truncation — a stale 59 would make swipe navigation target a
        // missing page.
        string pages = string.Join(",", Enumerable.Range(0, 60).Select(i => $"{{\"PageName\":\"P{i}\"}}"));
        var loaded = ProfileOps.ImportJson($$$"""{"ProfileId":"x","Pages":[{{{pages}}}],"ActivePageIndex":59}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(50, loaded.Pages.Count);
        Assert.AreEqual(49, loaded.ActivePageIndex, "The active index must clamp to the last page after truncation");
    }

    [TestMethod]
    public void ImportJson_NullPageElements_AreSkipped()
    {
        // "Pages":[null] deserializes as null ELEMENTS — the sanitizer must
        // drop them instead of NRE-ing on the page list it exists for.
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[null,{"PageName":"A"},null,{"PageName":"B"}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded.Pages.Count);
        Assert.AreEqual("A", loaded.Pages[0].PageName);
        Assert.AreEqual("B", loaded.Pages[1].PageName);
    }

    [TestMethod]
    public void ImportJson_NullWidgetElements_AreSkipped()
    {
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[null,{"PluginId":"profile_test_widget"},null]}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages[0].Widgets.Count, "null widget elements must be dropped, not crash the sanitizer");
        Assert.AreEqual("profile_test_widget", loaded.Pages[0].Widgets[0].PluginId);
    }

    [TestMethod]
    public void ImportJson_CrlfChannelName_IsCleared()
    {
        // An imported channel with an embedded CRLF would inject extra IRC
        // lines on connect — the sanitizer must clear it so the widget's
        // empty-channel fallback applies.
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"profile_test_widget","PropertyValues":{"ChannelName":"x\r\nPRIVMSG #popular :spam"}}]}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual("", loaded.Pages[0].Widgets[0].PropertyValues["ChannelName"],
            "a CRLF-bearing channel name must be cleared on import");
    }

    [TestMethod]
    public void ImportJson_OverLengthChannelName_IsCleared()
    {
        // An over-25-char channel name is invalid on Twitch — clear it so the
        // widget's empty-channel fallback applies.
        string longChannel = new string('a', 40);
        var loaded = ProfileOps.ImportJson($$$"""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"profile_test_widget","PropertyValues":{"ChannelName":"{{{longChannel}}}"}}]}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual("", loaded.Pages[0].Widgets[0].PropertyValues["ChannelName"]);
    }

    // ── InstanceId safety (the weather cache-file key) ─────

    [TestMethod]
    public void ImportJson_UnsafeInstanceId_IsRegenerated()
    {
        // The placed InstanceId flows into the weather widget's cache file
        // name ("weather_{InstanceId}.json") — a foreign profile with path
        // segments would escape the cache directory on the next fetch.
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"profile_test_widget","InstanceId":"..\\..\\evil","PropertyValues":{}}]}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.IsTrue(ProfileImportSanitizer.IsSafeInstanceId(loaded.Pages[0].Widgets[0].InstanceId),
            "an escaping InstanceId must be regenerated to a safe token");
        Assert.AreNotEqual(@"..\..\evil", loaded.Pages[0].Widgets[0].InstanceId);
    }

    [TestMethod]
    public void ImportJson_SafeInstanceId_IsPreserved()
    {
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"profile_test_widget","InstanceId":"ab-12_CD","PropertyValues":{}}]}]}""", CreateLoader(), new TestContext());

        Assert.IsNotNull(loaded);
        Assert.AreEqual("ab-12_CD", loaded.Pages[0].Widgets[0].InstanceId,
            "a safe token (letters, digits, '-', '_') must survive import unchanged");
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
        // The widget declares Size5x4 in its [WidgetMetadata]: the catalog
        // entry reports the full-framebuffer size, so placement takes the
        // origin branch — no probe instance is constructed.
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
        loader.RegisterBuiltInPlugin(typeof(TestWidget)); // 406 x 148 (the fixture declares the 2x1 preset in its metadata; placement reads the catalog entry)

        var placed = ProfileOps.PlaceCentered(profile, loader, new TestContext(), "profile_test_widget");

        Assert.IsNotNull(placed);
        // The center snaps through the shared rule — the same float math the
        // drag/resize snap uses, so placement and manipulation agree at the
        // half-cell boundary.
        float cx = GridSizeExtensions.SnapX(DisplayGeometry.FramebufferWidth / 2.0f);
        float cy = GridSizeExtensions.SnapY(DisplayGeometry.FramebufferHeight / 2.0f);
        Assert.AreEqual(cx - 406f / 2, placed.X);
        Assert.AreEqual(cy - 148f / 2, placed.Y);
    }


    [TestMethod]
    public void TryGetPage_InRange_ReturnsThePage()
    {
        var profile = new ProfileLayout();
        PageLayout added = ProfileOps.AddPage(profile, "Second");

        Assert.AreSame(added, ProfileOps.TryGetPage(profile, 1),
            "a valid index hands back the page the caller can read facts from");
    }

    [TestMethod]
    public void TryGetPage_OutOfRange_ReturnsNull()
    {
        var profile = new ProfileLayout();

        Assert.IsNull(ProfileOps.TryGetPage(profile, -1));
        Assert.IsNull(ProfileOps.TryGetPage(profile, profile.Pages.Count));
        Assert.IsNull(ProfileOps.TryGetPage(profile, profile.Pages.Count + 42),
            "a stale window index degrades to null instead of throwing");
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
    public void ImportJson_OmittedSize_RehydratesAtTheWidgetsDeclaredPreset()
    {
        // Hand-crafted JSON (the export always writes explicit sizes, so this
        // is the only path that can omit them): a size-less placement of the
        // Size5x4-declared widget rehydrates at the declared preset, not the
        // model's 2×2 default.
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(FullScreenTestWidget));
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"fullscreen_test_widget"}]}]}""", loader, new TestContext());

        var placed = loaded!.ActivePage.Widgets.Single();
        Assert.IsNotNull(placed.ActiveInstance, "Rehydration must succeed for the declared preset to apply");
        Assert.AreEqual(GridSizePreset.Size5x4.ToSize().Width, placed.Width, "the omitted width must fall back to the widget's declared preset");
        Assert.AreEqual(GridSizePreset.Size5x4.ToSize().Height, placed.Height, "the omitted height must fall back to the widget's declared preset");
    }

    [TestMethod]
    public void ImportJson_ExplicitSize_WinsOverTheDeclaredPreset()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(FullScreenTestWidget));
        var loaded = ProfileOps.ImportJson("""{"ProfileId":"x","Pages":[{"PageName":"A","Widgets":[{"PluginId":"fullscreen_test_widget","Width":406,"Height":296}]}]}""", loader, new TestContext());

        var placed = loaded!.ActivePage.Widgets.Single();
        Assert.AreEqual(406, placed.Width, "an explicit width must survive import");
        Assert.AreEqual(296, placed.Height, "an explicit height must survive import");
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

    // ── profile replacement (import swap) ───────────────────

    [TestMethod]
    public void ReplaceProfile_DisposesOldProfileAndReturnsImported()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(DisposableTestWidget));
        var current = new ProfileLayout();
        var oldWidget = new DisposableTestWidget();
        current.ActivePage.Widgets.Add(new PlacedWidgetInstance { PluginId = "disposable_test_widget", ActiveInstance = oldWidget });

        var imported = new ProfileLayout();
        imported.ActivePage.SnapToGrid = false;

        var result = ProfileOps.ReplaceProfile(current, imported);

        Assert.AreSame(imported, result, "The imported profile must become the active one");
        Assert.IsTrue(oldWidget.Disposed, "ReplaceProfile must dispose the old profile's widget instances");
        Assert.IsFalse(result.ActivePage.SnapToGrid, "The imported profile's snap-to-grid must surface untouched");
    }

    [TestMethod]
    public void ReplaceProfile_SurfacesImportedSnapToGrid()
    {
        var loader = CreateLoader();
        var context = new TestContext();
        var current = CreateProfile(loader, context);

        var imported = new ProfileLayout();
        imported.ActivePage.SnapToGrid = true;

        var result = ProfileOps.ReplaceProfile(current, imported);

        Assert.AreSame(imported, result, "The swap must return the imported profile active");
        Assert.IsTrue(result.ActivePage.SnapToGrid, "The import's snap-to-grid must be readable off the result");
        Assert.AreNotSame(current, result, "The old profile must no longer be active");
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
    public void ImportJson_WhitespaceOnlyActionCommand_IsCleared()
    {
        // A whitespace-only command can't arm anything, but normalizing it to
        // empty keeps the "imports never carry a command" invariant airtight.
        var loader = CreateLoader();
        var context = new TestContext();
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Main");
        var placed = ProfileOps.PlaceWidget(profile, loader, context, "profile_test_widget", 0, 0);
        placed!.PropertyValues["ActionCommand"] = "   ";
        string json = ProfileOps.ExportJson(profile);

        var loaded = ProfileOps.ImportJson(json, loader, context);

        var imported = loaded!.Pages[1].Widgets[0];
        Assert.AreEqual("", imported.PropertyValues["ActionCommand"], "Whitespace-only ActionCommand must be normalized to empty");
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

    [TestMethod]
    public void PageLayout_PageName_EnforcesTrim()
    {
        var page = new PageLayout
        {
            PageName = "   My Dashboard   "
        };

        Assert.AreEqual("My Dashboard", page.PageName);
    }

    [TestMethod]
    public void ProfileLayout_Serialization_RoundTripsSuccessfully()
    {
        var profile = new ProfileLayout
        {
            ProfileName = "   Custom Profile   "
        };
        profile.ActivePage.Widgets.Add(new PlacedWidgetInstance
        {
            PluginId = "clock_modern",
            DisplayName = "  Clock Widget  ",
            Width = 408f,
            Height = 150f,
            Opacity = 1.5f
        });

        Assert.AreEqual("Custom Profile", profile.ProfileName);
        Assert.AreEqual("Clock Widget", profile.ActivePage.Widgets[0].DisplayName);
        Assert.AreEqual(1.0f, profile.ActivePage.Widgets[0].Opacity, "Opacity should clamp to 1.0");

        string json = JsonSerializer.Serialize(profile);
        Assert.IsFalse(string.IsNullOrEmpty(json));

        var deserialized = JsonSerializer.Deserialize<ProfileLayout>(json);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("Custom Profile", deserialized.ProfileName);
        Assert.AreEqual(1, deserialized.Pages[0].Widgets.Count);
        Assert.AreEqual("Clock Widget", deserialized.Pages[0].Widgets[0].DisplayName);
    }

    [TestMethod]
    public void PlacedWidgetInstance_PropertyValues_RoundTripMixedTypes()
    {
        var placed = new PlacedWidgetInstance
        {
            PluginId = "stopwatch_timer",
            PropertyValues =
            {
                ["TextColorHex"] = "#FFCD85",
                ["CornerRadius"] = 16f,
                ["ShowChange"] = true,
                ["PriceDecimals"] = "4"
            }
        };

        string json = JsonSerializer.Serialize(placed);
        var deserialized = JsonSerializer.Deserialize<PlacedWidgetInstance>(json);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(4, deserialized.PropertyValues.Count);
        Assert.IsTrue(deserialized.PropertyValues.ContainsKey("TextColorHex"));
        Assert.AreEqual(JsonValueKind.Number, ((JsonElement)deserialized.PropertyValues["CornerRadius"]!).ValueKind, "Imported numbers should arrive as JsonElement");
    }
}
