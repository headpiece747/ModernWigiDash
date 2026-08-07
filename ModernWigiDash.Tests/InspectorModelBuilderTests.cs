using ModernWigiDash.App.Inspector;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class InspectorModelBuilderTests
{
    private sealed class FakeWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
    {
        [WidgetProperty("Title", WidgetPropertyType.Text, defaultValue: "Hello")]
        public string Title { get; set; } = "Hello";

        [WidgetProperty("Size", WidgetPropertyType.Number, defaultValue: 24.0)]
        public double Size { get; set; } = 24.0;

        [WidgetProperty("Visible", WidgetPropertyType.Boolean, defaultValue: true)]
        public bool Visible { get; set; } = true;

        [WidgetProperty("Color", WidgetPropertyType.Color, defaultValue: "#FF0000")]
        public string Color { get; set; } = "#FF0000";

        [WidgetProperty("Mode", WidgetPropertyType.Choice, "", "auto", "auto", "manual", "off")]
        public string Mode { get; set; } = "auto";

        [WidgetProperty("DoThing", WidgetPropertyType.Button)]
        public string DoThing { get; set; } = "";

        public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
            => propertyName == nameof(Mode)
                ? [new WidgetPropertyOption("turbo", "Turbo"), new WidgetPropertyOption("eco", "Eco")]
                : [];

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }

    private static PlacedWidgetInstance Place(FakeWidget? widget = null) => new()
    {
        PluginId = "fake",
        DisplayName = "Fake",
        ActiveInstance = widget ?? new FakeWidget()
    };

    [TestMethod]
    public void Describe_ReturnsEntryPerWidgetProperty()
    {
        var descriptions = InspectorModelBuilder.Describe(Place());

        Assert.AreEqual(6, descriptions.Count);
        CollectionAssert.AreEqual(
            new[] { "Title", "Size", "Visible", "Color", "Mode", "DoThing" },
            descriptions.Select(d => d.Property.Name).ToArray());
    }

    [TestMethod]
    public void Describe_ActionProperty_IsMarkedAsAction()
    {
        var descriptions = InspectorModelBuilder.Describe(Place());

        var action = descriptions.Single(d => d.Property.Name == "DoThing");
        Assert.IsTrue(action.IsAction);
        Assert.AreEqual(WidgetPropertyType.Button, action.PropertyType);
        Assert.IsFalse(descriptions.Single(d => d.Property.Name == "Title").IsAction);
    }

    [TestMethod]
    public void Describe_CurrentValue_DefaultsToAttributeDefault()
    {
        var widget = new FakeWidget { Title = "Changed" };
        var descriptions = InspectorModelBuilder.Describe(Place(widget));

        Assert.AreEqual("Changed", descriptions.Single(d => d.Property.Name == "Title").CurrentValue);
    }

    [TestMethod]
    public void Describe_Choice_UsesOptionsProviderOverAttributeOptions()
    {
        var descriptions = InspectorModelBuilder.Describe(Place());

        var mode = descriptions.Single(d => d.Property.Name == "Mode");
        CollectionAssert.AreEqual(
            new[] { "turbo", "eco" },
            mode.Options.Select(o => o.Value).ToArray());
    }

    [TestMethod]
    public void Describe_NoActiveInstance_ReturnsEmpty()
    {
        var placed = new PlacedWidgetInstance { PluginId = "x", DisplayName = "X", ActiveInstance = null };

        Assert.AreEqual(0, InspectorModelBuilder.Describe(placed).Count);
    }

    [TestMethod]
    public void Describe_HotkeyWidget_HidesIconFileCompanion()
    {
        var hotkey = new HotkeyButtonWidget();
        var placed = new PlacedWidgetInstance
        {
            PluginId = "hotkey",
            DisplayName = "Hotkey",
            ActiveInstance = hotkey
        };

        var descriptions = InspectorModelBuilder.Describe(placed);

        Assert.IsFalse(descriptions.Any(d => d.Property.Name == nameof(HotkeyButtonWidget.IconFile)),
            "IconFile is a hidden companion of the Icon editor");
        Assert.IsTrue(descriptions.Any(d => d.Property.Name == nameof(HotkeyButtonWidget.Icon)));
        Assert.IsTrue(descriptions.Any(d => d.Property.Name == nameof(HotkeyButtonWidget.ActionCommand)));
    }

    [TestMethod]
    public void Describe_SensorSelector_OptionsComeFromLiveStore()
    {
        LhmSensorStore.Reset();
        LhmSensorStore.Update(new LhmSnapshot(true, DateTime.UtcNow,
            [
                new LhmReading("cpu", "CPU Temp", "Mainboard: CPU Temp", "°C", 50, 40, 90, 52),
                new LhmReading("gpu", "GPU Temp", "GPU: GPU Temp", "°C", 60, 40, 90, 55)
            ]));

        var sensorWidget = new SensorSelectorWidgetStub();
        var descriptions = InspectorModelBuilder.Describe(new PlacedWidgetInstance
        {
            PluginId = "sensor",
            DisplayName = "Sensor",
            ActiveInstance = sensorWidget
        });

        var selector = descriptions.Single(d => d.PropertyType == WidgetPropertyType.SensorSelector);
        CollectionAssert.AreEqual(
            new[] { "GPU: GPU Temp", "Mainboard: CPU Temp" },
            selector.Options.Select(o => o.Value).ToArray());
    }

    private sealed class SensorSelectorWidgetStub : ModernWidgetBase
    {
        [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector)]
        public string SensorLabel { get; set; } = "";

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }
}
