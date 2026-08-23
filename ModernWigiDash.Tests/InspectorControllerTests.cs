using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App.Inspector;
using ModernWigiDash.App.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// Inspector controller tests through the two narrow facet bindings. Both
/// bindings are plain holders of WPF controls, so the fakes are built from
/// throwaway controls on an STA thread (WPF objects require STA).
/// </summary>
[TestClass]
public class InspectorControllerTests
{
    [TestMethod]
    public void Refresh_WithSelection_PopulatesFacetControls()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (transform, panel, select, placed, _) = BuildHost();
            var controller = BuildController(owner, transform, panel, select);
            placed.DisplayName = "My Widget";
            placed.X = 42;
            placed.Y = 7;

            controller.Refresh();

            Assert.AreEqual("My Widget", panel.NameText.Text);
            Assert.AreEqual("42", transform.PosX.Text);
            Assert.AreEqual("7", transform.PosY.Text);
            Assert.AreEqual(Visibility.Collapsed, panel.EmptyPanel.Visibility);
            Assert.AreEqual(Visibility.Visible, panel.ActivePanel.Visibility);
        });
    }

    [TestMethod]
    public void Refresh_WithoutSelection_ShowsEmptyPanel()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (transform, panel, select, _, _) = BuildHost(select: () => null);
            var controller = BuildController(owner, transform, panel, select);

            controller.Refresh();

            Assert.AreEqual(Visibility.Visible, panel.EmptyPanel.Visibility);
            Assert.AreEqual(Visibility.Collapsed, panel.ActivePanel.Visibility);
        });
    }

    [TestMethod]
    public void ApplyPropertyValue_WritesInstanceAndPropertyValues()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (transform, panel, select, placed, widget) = BuildHost();
            var controller = BuildController(owner, transform, panel, select);
            PropertyInfo prop = typeof(TestWidget).GetProperty(nameof(TestWidget.Label))!;

            controller.ApplyPropertyValue(prop, "updated");

            Assert.AreEqual("updated", widget.Label);
            Assert.AreEqual("updated", placed.PropertyValues[nameof(TestWidget.Label)],
                "the write-back seam must persist into the placed instance's PropertyValues");
        });
    }

    [TestMethod]
    public void TransformChanged_LandedWriteBack_FiresProfileChangedExactlyOnce()
    {
        // The single dirty mark on the inspector-driven path is the
        // onProfileChanged callback: a landed write-back fires it once, and
        // the window's forwarding handler adds no second mark (the old
        // doubled marks are gone).
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (transform, panel, select, _, _) = BuildHost();
            int marks = 0;
            var controller = BuildController(owner, transform, panel, select, onProfileChanged: () => marks++);

            transform.PosX.Text = "42";

            controller.TransformChanged(transform.PosX, TextChangedEvent());

            Assert.AreEqual(1, marks, "a landed write-back must arm profile persistence exactly once");
        });
    }

    [TestMethod]
    public void OpacityChanged_LandedWriteBack_FiresProfileChangedExactlyOnce()
    {
        StaRunner.Run(() =>
        {
            var owner = new Window();
            var (transform, panel, select, _, _) = BuildHost();
            int marks = 0;
            var controller = BuildController(owner, transform, panel, select, onProfileChanged: () => marks++);

            transform.OpacitySlider.Value = 0.7;

            controller.OpacityChanged(transform.OpacitySlider,
                new RoutedPropertyChangedEventArgs<double>(0.0, 0.7));

            Assert.AreEqual(1, marks, "a landed opacity write-back must arm profile persistence exactly once");
        });
    }

    [TestMethod]
    public void TransformChanged_NoSelection_FiresNoMark()
    {
        // Suppressed (no selection) means silent: the rebuild guard dropping a
        // write-back arms nothing, so the mark count and the repaint request
        // both stay at zero — no spurious save from an invalid keystroke.
        StaRunner.Run(() =>
        {
            var owner = new Window();
            int renders = 0;
            var (transform, panel, select, _, _) = BuildHost(select: () => null);
            transform = new TransformFieldBindings(
                transform.PosX, transform.PosY, transform.WidthText, transform.HeightText,
                transform.ZIndexText, transform.RotationText, transform.OpacitySlider,
                transform.OpacityValueText, () => renders++);
            int marks = 0;
            var controller = BuildController(owner, transform, panel, select, onProfileChanged: () => marks++);

            transform.PosX.Text = "not a number";

            controller.TransformChanged(transform.PosX, TextChangedEvent());

            Assert.AreEqual(0, marks, "a suppressed write-back must not arm persistence");
            Assert.AreEqual(0, renders, "a suppressed write-back must not request a canvas repaint");
        });
    }

    [TestMethod]
    public void TransformChanged_UnparseableInput_FiresNoMarkAndNoRender()
    {
        // The save mark is armed only when a value actually landed: an
        // unparseable keystroke (a mid-edit "-") must not dirty the profile or
        // repaint — the old code re-parsed all six boxes and armed the
        // debounce on every box change, parsed or not.
        StaRunner.Run(() =>
        {
            var owner = new Window();
            int renders = 0;
            var (transform, panel, select, _, _) = BuildHost();
            transform = new TransformFieldBindings(
                transform.PosX, transform.PosY, transform.WidthText, transform.HeightText,
                transform.ZIndexText, transform.RotationText, transform.OpacitySlider,
                transform.OpacityValueText, () => renders++);
            int marks = 0;
            var controller = BuildController(owner, transform, panel, select, onProfileChanged: () => marks++);

            transform.PosX.Text = "not a number";

            controller.TransformChanged(transform.PosX, TextChangedEvent());

            Assert.AreEqual(0, marks, "unparseable input must not arm persistence");
            Assert.AreEqual(0, renders, "unparseable input must not request a canvas repaint");
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
            var (transform, panel, select, _, _) = BuildHost();
            var controller = BuildController(owner, transform, panel, select);
            controller.Refresh();

            // Host the custom-properties panel in a real shown window so the
            // editors can take keyboard focus (Focus needs a PresentationSource).
            var window = new Window { Content = panel.CustomProperties, Width = 300, Height = 200 };
            window.Show();
            window.UpdateLayout();
            try
            {
                // The Label property renders one row with a TextBox editor.
                var labelRow = (StackPanel)panel.CustomProperties.Children[0];
                var labelEditor = labelRow.Children.OfType<TextBox>().Single();
                labelEditor.Text = "springfield";
                labelEditor.CaretIndex = 5; // mid-word: "sprin|gfield"
                labelEditor.Focus();
                Assert.IsTrue(labelEditor.IsKeyboardFocused, "precondition: the editor must own focus");
                Assert.AreEqual(5, labelEditor.CaretIndex, "precondition: caret is mid-word");

                controller.Refresh();

                var rebuiltRow = (StackPanel)panel.CustomProperties.Children[0];
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

    // ── fake bindings builder ──────────────────────────────────

    private static (TransformFieldBindings Transform, CustomPropertyPanel Panel, Func<PlacedWidgetInstance?> Select,
        PlacedWidgetInstance Placed, TestWidget Widget) BuildHost(
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
        var transform = new TransformFieldBindings(
            new TextBox(),
            new TextBox(),
            new TextBox(),
            new TextBox(),
            new TextBox(),
            new TextBox(),
            new Slider(),
            new TextBlock(),
            () => { });
        var panel = new CustomPropertyPanel(
            new StackPanel(),
            new StackPanel(),
            new TextBlock(),
            new StackPanel(),
            tryFindResource: _ => null);
        return (transform, panel, select ?? (() => placed), placed, widget);
    }

    private static InspectorController BuildController(
        Window owner,
        TransformFieldBindings transform,
        CustomPropertyPanel panel,
        Func<PlacedWidgetInstance?> select,
        Action? onProfileChanged = null)
        => new(transform, panel, select,
            new DialogHost(owner, new ThemeApplicator(), _ => null, (_, _) => { }), onProfileChanged);

    /// <summary>The controller ignores the event args (it reads the control
    /// values), so the tests pass one throwaway event.</summary>
    private static TextChangedEventArgs TextChangedEvent()
        => new(TextBox.TextChangedEvent, UndoAction.Undo, changes: null);
}
