using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ModernWigiDash.App.Inspector;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class InspectorPanelRendererTests
{
    [TestMethod]
    public void Render_LocationSearchWidget_BuildsSearchEditorAndCommitsPick()
    {
        StaRunner.Run(() =>
        {
            var widget = new WeatherForecastWidget();
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            var descriptions = InspectorModelBuilder.Describe(placed);
            var target = new StackPanel();
            GeocodeCandidate? committed = null;
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
                CommitLocationPick = c => committed = c,
            };

            InspectorPanelRenderer.Render(placed, descriptions, target.Children, () => false, callbacks);

            // The Location row hosts the search editor: its results ListBox lives
            // in the popup's Child, which is not in the visual tree while the
            // popup is closed — reach it through the Popup, then select a
            // candidate and assert the commit callback ran.
            var popup = FindVisualChildren<Popup>(target).First();
            var listBox = FindVisualChildren<ListBox>((DependencyObject)popup.Child!).First();
            listBox.ItemsSource = new[] { new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508) };
            listBox.SelectedItem = listBox.Items[0];

            Assert.IsNotNull(committed, "picking from the search list must reach the commit callback");
            Assert.AreEqual("Berlin, New Hampshire, United States", committed!.Label);
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
