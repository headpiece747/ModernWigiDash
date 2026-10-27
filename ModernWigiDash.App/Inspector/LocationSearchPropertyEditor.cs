using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Custom property editor for debounced location search (<see cref="EditorKind.LocationSearch"/>).
/// Renders a search-as-you-type TextBox with a results Popup and commits selected candidates.
/// </summary>
internal sealed class LocationSearchPropertyEditor : ICustomPropertyEditor
{
    public EditorKind Kind => EditorKind.LocationSearch;

    public UIElement BuildEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        if (widget.ActiveInstance is not IWidgetLocationSearch search)
        {
            // Degrade gracefully if the widget doesn't implement the contract
            var fallbackBox = new TextBox { Text = desc.CurrentValue?.ToString() ?? string.Empty };
            fallbackBox.TextChanged += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, fallbackBox.Text);
            return fallbackBox;
        }

        string baseLabel = desc.CurrentValue?.ToString() ?? "";
        string seed = LocationSearchModel.SeedText(baseLabel, search.CurrentPopulation);
        var box = new TextBox { Text = seed };
        var results = new ListBox
        {
            MaxHeight = 160,
            Visibility = Visibility.Collapsed,
            ItemTemplate = BuildCandidateLineTemplate()
        };
        var content = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = results
        };
        var popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = content
        };

        var version = new SearchVersionToken();
        bool popupPressed = false;
        bool syncingText = false;

        content.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => popupPressed = true), handledEventsToo: true);
        box.GotFocus += (_, _) => popupPressed = false;

        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            string query = LocationSearchModel.QueryFor(box.Text, seed, baseLabel);
            var (outcome, candidates) = await LocationSearchModel.RunSearchTickAsync(search, query, version);
            ApplySearchResults(results, popup, outcome, candidates);
        };
        box.TextChanged += (_, _) =>
        {
            if (syncingText) return;
            debounce.Stop();
            debounce.Start();
        };
        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is GeocodeCandidate picked)
            {
                popup.IsOpen = false;
                results.Visibility = Visibility.Collapsed;
                syncingText = true;
                try { box.Text = picked.Label; }
                finally { syncingText = false; }
                callbacks.CommitLocationPick?.Invoke(picked);
            }
        };
        void CommitTypedText()
        {
            string committed = LocationSearchModel.CommitText(box.Text, seed, baseLabel);
            callbacks.ApplyInspectorPropertyValue(desc.Property, committed);
            popup.IsOpen = false;
            results.Visibility = Visibility.Collapsed;
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitTypedText();
        };
        box.LostFocus += (_, _) =>
        {
            if (!LocationSearchModel.ShouldCommitOnLostFocus(popupPressed, popup.IsOpen && popup.IsMouseOver)) return;
            CommitTypedText();
        };

        return new StackPanel { Children = { box, popup } };
    }

    private static DataTemplate BuildCandidateLineTemplate()
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new MultiBinding
        {
            Converter = new CandidateLineConverter(),
            Bindings = { new Binding(nameof(GeocodeCandidate.Label)), new Binding(nameof(GeocodeCandidate.Population)) }
        });
        text.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        text.SetValue(TextBlock.FontSizeProperty, 12.0);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2));
        template.VisualTree = text;
        return template;
    }

    /// <summary>
    /// Applies search results to the ListBox and Popup.
    /// </summary>
    public static void ApplySearchResults(ListBox results, Popup popup, LocationSearchTick outcome, IReadOnlyList<GeocodeCandidate>? candidates)
    {
        if (outcome == LocationSearchTick.Stale) return;
        if (outcome == LocationSearchTick.NoSearch || candidates is null || candidates.Count == 0)
        {
            results.ItemsSource = null;
            results.Visibility = Visibility.Collapsed;
            popup.IsOpen = false;
            return;
        }

        results.ItemsSource = candidates;
        results.Visibility = Visibility.Visible;
        popup.IsOpen = true;
    }

    /// <summary>
    /// Formats candidate display line with label and compact population suffix.
    /// </summary>
    public sealed class CandidateLineConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            string label = values[0] as string ?? "";
            double population = values[1] is double p ? p : 0;
            return population > 0 ? $"{label} · {LocationSearchModel.FormatPopulation(population)}" : label;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
