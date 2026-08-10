using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App;
using ModernWigiDash.App.Inspector;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// Inspector controller tests through a fake host. InspectorControllerHost is
/// a plain holder of WPF controls, so the fake host is built from throwaway
/// controls on an STA thread (WPF objects require STA).
/// </summary>
[TestClass]
public class InspectorControllerTests
{
    private sealed class TestWidget : ModernWidgetBase
    {
        [WidgetProperty("Label", WidgetPropertyType.Text, defaultValue: "seed")]
        public string Label { get; set; } = "seed";

        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    [TestMethod]
    public void Refresh_WithSelection_PopulatesHostControls()
    {
        RunOnSta(() =>
        {
            var owner = new Window();
            var (host, placed, _) = BuildHost();
            var controller = new InspectorController(host, new DialogHost(owner, _ => null, (_, _) => { }));
            placed.DisplayName = "My Widget";
            placed.X = 42;
            placed.Y = 7;

            controller.Refresh();

            Assert.AreEqual("My Widget", host.NameText.Text);
            Assert.AreEqual("42", host.PosX.Text);
            Assert.AreEqual("7", host.PosY.Text);
            Assert.AreEqual(Visibility.Collapsed, host.EmptyPanel.Visibility);
            Assert.AreEqual(Visibility.Visible, host.ActivePanel.Visibility);
        });
    }

    [TestMethod]
    public void Refresh_WithoutSelection_ShowsEmptyPanel()
    {
        RunOnSta(() =>
        {
            var owner = new Window();
            var (host, _, _) = BuildHost(select: () => null);
            var controller = new InspectorController(host, new DialogHost(owner, _ => null, (_, _) => { }));

            controller.Refresh();

            Assert.AreEqual(Visibility.Visible, host.EmptyPanel.Visibility);
            Assert.AreEqual(Visibility.Collapsed, host.ActivePanel.Visibility);
        });
    }

    [TestMethod]
    public void ApplyPropertyValue_WritesInstanceAndPropertyValues()
    {
        RunOnSta(() =>
        {
            var owner = new Window();
            var (host, placed, widget) = BuildHost();
            var controller = new InspectorController(host, new DialogHost(owner, _ => null, (_, _) => { }));
            PropertyInfo prop = typeof(TestWidget).GetProperty(nameof(TestWidget.Label))!;

            controller.ApplyPropertyValue(prop, "updated");

            Assert.AreEqual("updated", widget.Label);
            Assert.AreEqual("updated", placed.PropertyValues[nameof(TestWidget.Label)],
                "the write-back seam must persist into the placed instance's PropertyValues");
        });
    }

    // ── fake host builder ──────────────────────────────────────

    private static (InspectorControllerHost Host, PlacedWidgetInstance Placed, TestWidget Widget) BuildHost(
        Func<PlacedWidgetInstance?>? select = null)
    {
        var widget = new TestWidget();
        var placed = new PlacedWidgetInstance
        {
            PluginId = "test",
            DisplayName = "Test Widget",
            ActiveInstance = widget,
            PropertyValues = []
        };
        var host = new InspectorControllerHost(
            emptyPanel: new StackPanel(),
            activePanel: new StackPanel(),
            nameText: new TextBlock(),
            posX: new TextBox(),
            posY: new TextBox(),
            widthText: new TextBox(),
            heightText: new TextBox(),
            zIndexText: new TextBox(),
            rotationText: new TextBox(),
            opacitySlider: new Slider(),
            opacityValueText: new TextBlock(),
            customProperties: new StackPanel(),
            tryFindResource: _ => null,
            getSelectedWidget: select ?? (() => placed),
            requestCanvasRender: () => { });
        return (host, placed, widget);
    }

    private static void RunOnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error != null)
        {
            Assert.Fail($"STA work failed: {error}");
        }
    }
}
