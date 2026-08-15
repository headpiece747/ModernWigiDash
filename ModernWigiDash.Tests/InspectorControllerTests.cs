using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App;
using ModernWigiDash.App.Inspector;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Tests;

/// <summary>
/// Inspector controller tests through a fake host. InspectorControllerHost is
/// a plain holder of WPF controls, so the fake host is built from throwaway
/// controls on an STA thread (WPF objects require STA).
/// </summary>
[TestClass]
public class InspectorControllerTests
{
    [TestMethod]
    public void Refresh_WithSelection_PopulatesHostControls()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (host, placed, _) = BuildHost();
            var controller = new InspectorController(host, new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }));
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
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (host, _, _) = BuildHost(select: () => null);
            var controller = new InspectorController(host, new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }));

            controller.Refresh();

            Assert.AreEqual(Visibility.Visible, host.EmptyPanel.Visibility);
            Assert.AreEqual(Visibility.Collapsed, host.ActivePanel.Visibility);
        });
    }

    [TestMethod]
    public void ApplyPropertyValue_WritesInstanceAndPropertyValues()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (host, placed, widget) = BuildHost();
            var controller = new InspectorController(host, new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }));
            PropertyInfo prop = typeof(TestWidget).GetProperty(nameof(TestWidget.Label))!;

            controller.ApplyPropertyValue(prop, "updated");

            Assert.AreEqual("updated", widget.Label);
            Assert.AreEqual("updated", placed.PropertyValues[nameof(TestWidget.Label)],
                "the write-back seam must persist into the placed instance's PropertyValues");
        });
    }

    [TestMethod]
    public void Refresh_WithFocusedPropertyEditor_PreservesFocusAndCaretAcrossRebuild()
    {
        // The weather widget's inspector refresh fires while the user is still
        // typing in Location: the rebuild must return focus (and the caret) to
        // the same property's editor, or every keystroke kicks the user out of
        // the box or jumps the caret back to the start of the word.
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (host, _, _) = BuildHost();
            var controller = new InspectorController(host, new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }));
            controller.Refresh();

            // Host the custom-properties panel in a real shown window so the
            // editors can take keyboard focus (Focus needs a PresentationSource).
            var window = new Window { Content = host.CustomProperties, Width = 300, Height = 200 };
            window.Show();
            window.UpdateLayout();
            try
            {
                // The Label property renders one row with a TextBox editor.
                var labelRow = (StackPanel)host.CustomProperties.Children[0];
                var labelEditor = labelRow.Children.OfType<TextBox>().Single();
                labelEditor.Text = "springfield";
                labelEditor.CaretIndex = 5; // mid-word: "sprin|gfield"
                labelEditor.Focus();
                Assert.IsTrue(labelEditor.IsKeyboardFocused, "precondition: the editor must own focus");
                Assert.AreEqual(5, labelEditor.CaretIndex, "precondition: caret is mid-word");

                controller.Refresh();

                var rebuiltRow = (StackPanel)host.CustomProperties.Children[0];
                var rebuiltEditor = rebuiltRow.Children.OfType<TextBox>().Single();
                Assert.AreNotSame(labelEditor, rebuiltEditor, "the panel must be rebuilt (new editor instances)");
                Assert.IsTrue(rebuiltEditor.IsKeyboardFocused,
                    "focus must follow the rebuild to the same property's editor");
                Assert.AreEqual(5, rebuiltEditor.CaretIndex,
                    "the caret must stay where the user was typing, not jump to the start");
            }
            finally
            {
                window.Close();
            }
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
}
