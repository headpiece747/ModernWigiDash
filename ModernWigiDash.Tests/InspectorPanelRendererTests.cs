using System.Reflection;
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

    [TestMethod]
    public void Render_LocationSearchWidget_LostFocus_CommitsTypedText()
    {
        StaRunner.Run(() =>
        {
            var widget = new WeatherForecastWidget();
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            PropertyInfo? writtenProp = null;
            object? writtenValue = null;
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (prop, value) => { writtenProp = prop; writtenValue = value; },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            var target = new StackPanel();
            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed), target.Children, () => false, callbacks);

            // The Location row's editor: its TextBox is the row's only TextBox
            // (the results ListBox lives in the closed popup's Child).
            var locationRow = target.Children.OfType<StackPanel>().First(sp =>
                sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "Location");
            var box = FindVisualChildren<TextBox>(locationRow).Single();
            box.Text = "Berlin, NH";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.AreEqual(nameof(WeatherForecastWidget.Location), writtenProp?.Name,
                "focus loss must commit the typed text to the Location property");
            Assert.AreEqual("Berlin, NH", writtenValue);
        });
    }

    [TestMethod]
    public async Task RunSearchTickAsync_CompletedSearch_ReturnsCandidates()
    {
        var fake = new ScriptableLocationSearch();
        var version = new InspectorPanelRenderer.SearchVersionToken();

        var search = InspectorPanelRenderer.RunSearchTickAsync(fake, "Berlin", version);
        fake.Complete("Berlin", new GeocodeCandidate("Berlin, Germany", "Berlin, Germany", 52.52, 13.405));
        var (outcome, candidates) = await search;

        Assert.AreEqual(InspectorPanelRenderer.LocationSearchTick.Success, outcome);
        Assert.AreEqual(1, candidates!.Count);
        Assert.AreEqual("Berlin, Germany", candidates[0].Label);
    }

    [TestMethod]
    public async Task RunSearchTickAsync_ShortQueryTick_InvalidatesInFlightResponse()
    {
        var fake = new ScriptableLocationSearch();
        var version = new InspectorPanelRenderer.SearchVersionToken();

        var inFlight = InspectorPanelRenderer.RunSearchTickAsync(fake, "be", version);
        var (shortOutcome, _) = await InspectorPanelRenderer.RunSearchTickAsync(fake, "x", version);

        Assert.AreEqual(InspectorPanelRenderer.LocationSearchTick.NoSearch, shortOutcome,
            "a query shorter than two characters must not search");

        fake.Complete("be", new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508));
        var (staleOutcome, staleCandidates) = await inFlight;

        Assert.AreEqual(InspectorPanelRenderer.LocationSearchTick.Stale, staleOutcome,
            "a short-query tick must invalidate the response still in flight from the longer query");
        Assert.IsNull(staleCandidates);
    }

    /// <summary>
    /// Scriptable <see cref="IWidgetLocationSearch"/>: each SearchAsync call
    /// parks until <see cref="Complete"/> supplies the candidates, so tests can
    /// interleave ticks the way the debounced editor does.
    /// </summary>
    private sealed class ScriptableLocationSearch : IWidgetLocationSearch
    {
        private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<GeocodeCandidate>>> _pending = new();

        public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<GeocodeCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[query] = tcs;
            return tcs.Task;
        }

        public void Complete(string query, params GeocodeCandidate[] candidates)
            => _pending[query].TrySetResult(candidates);

        public void CommitPick(GeocodeCandidate candidate) { }
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
