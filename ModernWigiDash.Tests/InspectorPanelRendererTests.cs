using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.Tests;

[TestClass]
[DoNotParallelize]
public class InspectorPanelRendererTests
{
    [TestMethod]
    public void Render_LocationSearchWidget_BuildsSearchEditorAndCommitsPick()
    {
        StaRunner.Run(() =>
        {
            var widget = new WeatherForecastWidget();
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            var descriptions = InspectorModelBuilder.Describe(placed, []);
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

            InspectorPanelRenderer.Render(placed, descriptions, target.Children, callbacks);

            // The Location row hosts the search editor: its results ListBox lives
            // in the popup's Child, which is not in the visual tree while the
            // popup is closed — reach it through the Popup, then select a
            // candidate and assert the commit callback ran.
            var popup = VisualTree.FindDescendants<Popup>(target).First();
            var listBox = VisualTree.FindDescendants<ListBox>((DependencyObject)popup.Child!).First();
            var box = VisualTree.FindDescendants<TextBox>(target).First();
            listBox.ItemsSource = new[] { new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508) };
            listBox.SelectedItem = listBox.Items[0];

            Assert.IsNotNull(committed, "picking from the search list must reach the commit callback");
            Assert.AreEqual("Berlin, New Hampshire, United States", committed.Label);
            Assert.AreEqual("Berlin, New Hampshire, United States", box.Text,
                "the box must show the picked label so a later focus-loss commit cannot write the stale typed query");
        });
    }

    [TestMethod]
    public void Render_LocationSearchWidget_ResultsListShowsCandidatesWhenPopulated()
    {
        StaRunner.Run(() =>
        {
            var widget = new WeatherForecastWidget();
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            var target = new StackPanel();
            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            var popup = VisualTree.FindDescendants<Popup>(target).First();
            var results = VisualTree.FindDescendants<ListBox>((DependencyObject)popup.Child!).First();
            Assert.AreEqual(Visibility.Collapsed, results.Visibility, "the results list starts hidden");

            // Drive the tick decision through the extracted seam, then apply it
            // to the real controls exactly as the tick would.
            var version = new SearchVersionToken();
            var fake = new ScriptableLocationSearch();
            var search = LocationSearchModel.RunSearchTickAsync(fake, "Berlin", version);
            fake.Complete("Berlin", new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508) { Population = 10000 });
            var (outcome, candidates) = search.GetAwaiter().GetResult();
            InspectorPanelRenderer.ApplySearchResults(results, popup, outcome, candidates);

            Assert.AreEqual(LocationSearchTick.Success, outcome);
            Assert.AreEqual(Visibility.Visible, results.Visibility,
                "the results list must be visible when candidates arrive — a Collapsed list inside the popup renders nothing");
            Assert.IsNotNull(results.ItemTemplate, "candidates must render label + population, not the record's ToString");
            Assert.AreSame(candidates, results.ItemsSource);
        });
    }

    [TestMethod]
    public void Render_LocationSearchWidget_PopupPress_SuppressesLostFocusCommit()
    {
        StaRunner.Run(() =>
        {
            var widget = new WeatherForecastWidget();
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            PropertyInfo? writtenProp = null;
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (prop, _) => writtenProp = prop,
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            var target = new StackPanel();
            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            var popup = VisualTree.FindDescendants<Popup>(target).First();
            var box = VisualTree.FindDescendants<TextBox>(target).First();
            box.Text = "Berlin";

            // A mouse press inside the popup (a pick gesture) precedes the box's
            // focus loss; the LostFocus commit must be suppressed or the pick
            // dies mid-gesture.
            var content = popup.Child;
            content.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.IsNull(writtenProp, "a pick gesture inside the popup must not commit the typed text");

            // Refocusing the box clears the gesture mark: the next focus loss
            // commits normally.
            box.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent));
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.AreEqual(nameof(WeatherForecastWidget.Location), writtenProp?.Name,
                "after refocusing, focus loss must commit the typed text again");
        });
    }

    [TestMethod]
    public void Render_LocationSearchWidget_SeedsBoxWithLabelAndPopulation()
    {
        StaRunner.Run(() =>
        {
            // Seed the client's last-resolved population through the widget's
            // client seam: resolving the Berlin NH label sets it to 9367.
            var resolver = new StubHttpHandler(request =>
            {
                string url = request.RequestUri?.AbsoluteUri ?? "";
                if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleBerlines);
                return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherTestData.SampleForecastLegacy) : StubHttpHandler.NotFound();
            });
            var widget = new WeatherForecastWidget { Location = "Berlin, New Hampshire, United States" };
            widget.TestHttpClient = new HttpClient(resolver);
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            var profile = new ProfileLayout();
            profile.ActivePage.Widgets.Add(placed);
            var context = new PersistingContext(profile);
            widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
            // InitializeAsync kicks the startup fetch; wait for the boot apply
            // (the resolved population) before seeding from it.
            TestWait.WaitForApplied(() => widget.CurrentPopulation is > 0).GetAwaiter().GetResult();
            widget.SuppressLocationWriteback();
            widget.FetchLiveWeatherAsync().GetAwaiter().GetResult();
            Assert.AreEqual(9367, widget.CurrentPopulation, "precondition: the resolution must expose the population");

            var target = new StackPanel();
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
                CommitLocationPick = _ => { },
            };

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            var box = VisualTree.FindDescendants<TextBox>(target).First();
            Assert.IsTrue(box.Text.Contains("Berlin, New Hampshire, United States"), "the box must seed from the Location label");
            Assert.IsTrue(box.Text.Contains("9.4k"), "the box must append the population suffix from CurrentPopulation (shared formatter: 9367 → 9.4k)");
        });
    }

    [TestMethod]
    public void Render_LocationSearchWidget_LostFocus_CommitsBaseLabelNotPopulationSuffix()
    {
        StaRunner.Run(() =>
        {
            var resolver = new StubHttpHandler(request =>
            {
                string url = request.RequestUri?.AbsoluteUri ?? "";
                if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleBerlines);
                return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherTestData.SampleForecastLegacy) : StubHttpHandler.NotFound();
            });
            var widget = new WeatherForecastWidget { Location = "Berlin, New Hampshire, United States" };
            widget.TestHttpClient = new HttpClient(resolver);
            var placed = new PlacedWidgetInstance { PluginId = "weather", DisplayName = "Weather", ActiveInstance = widget };
            var profile = new ProfileLayout();
            profile.ActivePage.Widgets.Add(placed);
            var context = new PersistingContext(profile);
            widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
            TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 1, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            widget.SuppressLocationWriteback();
            widget.FetchLiveWeatherAsync().GetAwaiter().GetResult();
            Assert.AreEqual(9367, widget.CurrentPopulation, "precondition: the resolution must expose the population");

            var target = new StackPanel();
            object? committed = null;
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, value) => committed = value,
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            var box = VisualTree.FindDescendants<TextBox>(target).First();
            Assert.IsTrue(box.Text.Contains(" · 9.4k"), "precondition: the box must seed with the population suffix");
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            string committedText = (committed as string)!;
            Assert.IsFalse(committedText.Contains(" · "),
                "the committed Location must never carry the display-only population suffix");
            Assert.AreEqual("Berlin, New Hampshire, United States", committed,
                "focus loss on the seeded box must commit the base label");
        });
    }

    [TestMethod]
    public void CandidateLineConverter_FormatsLabelAndPopulation()
    {
        var converter = new InspectorPanelRenderer.CandidateLineConverter();

        string withPopulation = (string)converter.Convert(
            ["Berlin, New Hampshire, United States", 10000.0], typeof(string), null, CultureInfo.InvariantCulture);
        string withMillions = (string)converter.Convert(
            ["Berlin, Germany", 3_600_000.0], typeof(string), null, CultureInfo.InvariantCulture);
        string noPopulation = (string)converter.Convert(
            ["Berlin, New Hampshire, United States", 0.0], typeof(string), null, CultureInfo.InvariantCulture);

        Assert.AreEqual("Berlin, New Hampshire, United States · 10k", withPopulation);
        Assert.AreEqual("Berlin, Germany · 3.6M", withMillions);
        Assert.AreEqual("Berlin, New Hampshire, United States", noPopulation,
            "a candidate without population renders the bare label");
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
            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            // The Location row's editor: its TextBox is the row's only TextBox
            // (the results ListBox lives in the closed popup's Child).
            var locationRow = target.Children.OfType<StackPanel>().First(sp =>
                sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "Location");
            var box = VisualTree.FindDescendants<TextBox>(locationRow).Single();
            box.Text = "Berlin, NH";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.AreEqual(nameof(WeatherForecastWidget.Location), writtenProp?.Name,
                "focus loss must commit the typed text to the Location property");
            Assert.AreEqual("Berlin, NH", writtenValue);
        });
    }

    /// <summary>
    /// Scriptable <see cref="IWidgetLocationSearch"/>: each SearchAsync call
    /// parks until <see cref="Complete"/> supplies the candidates, so tests can
    /// interleave ticks the way the debounced editor does.
    /// </summary>
    internal sealed class ScriptableLocationSearch : IWidgetLocationSearch
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

        public double? CurrentPopulation { get; set; }
    }

    [TestMethod]
    public void Render_BooleanProperty_ToggleRoutesThroughTheWriteBackFunnel()
    {
        // The boolean editor is guarded like every other editor: its write-back
        // routes through the single ApplyInspectorPropertyValue funnel (where
        // the re-entrancy guard is enforced), so a CheckBox toggle lands in the
        // model exactly like a combo pick or a text edit.
        StaRunner.Run(() =>
        {
            var widget = new BooleanWidget();
            var placed = new PlacedWidgetInstance { PluginId = "bool", DisplayName = "Bool", ActiveInstance = widget };
            var target = new StackPanel();
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

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            var chk = VisualTree.FindDescendants<CheckBox>(target).Single();
            Assert.IsTrue(chk.IsChecked == true, "precondition: the editor seeds from the property's current value");
            chk.IsChecked = false;

            Assert.IsNotNull(writtenProp, "a CheckBox toggle must reach the write-back funnel");
            Assert.AreEqual(nameof(BooleanWidget.Flag), writtenProp.Name);
            Assert.IsFalse((bool)writtenValue!);
        });
    }

    [TestMethod]
    public void Render_RequiredChoiceWithoutOptions_ShowsTheHintAndNoTextEditor()
    {
        // The HWiNFO Sensor row persists a coded key (rt:id1:id2). While the
        // vendor list is still empty, the row must explain that instead of
        // offering a text box a user cannot fill correctly.
        StaRunner.Run(() =>
        {
            var widget = new RequiredChoiceWidget();
            var placed = new PlacedWidgetInstance { PluginId = "hwinfo", DisplayName = "HWiNFO", ActiveInstance = widget };
            var target = new StackPanel();
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            Assert.IsTrue(
                VisualTree.FindDescendants<TextBlock>(target).Any(t => t.Text == RequiredChoiceWidget.Hint),
                "the empty state must show the widget's hint");
            Assert.AreEqual(0, VisualTree.FindDescendants<TextBox>(target).Count(), "no free-text editor for a coded value");
            var placeholderCombo = VisualTree.FindDescendants<ComboBox>(target).Single();
            Assert.IsFalse(placeholderCombo.IsEnabled, "empty required Choice must keep a disabled placeholder dropdown");
            Assert.AreEqual("No options available", ((WidgetPropertyOption)placeholderCombo.Items[0]!).DisplayName);
        });
    }

    [TestMethod]
    public void Render_HwinfoFallbackOptions_ShowsDropdownAndStatusHint()
    {
        StaRunner.Run(() =>
        {
            ModernWigiDash.Hardware.Service.VendorService.SetInstance(null);
            try
            {
                var widget = new HwinfoWidget();
                var placed = new PlacedWidgetInstance { PluginId = "hwinfo", DisplayName = "HWiNFO Sensor", ActiveInstance = widget };
                var target = new StackPanel();
                var callbacks = new InspectorCallbacks
                {
                    TryFindResource = _ => null,
                    ApplyInspectorPropertyValue = (_, _) => { },
                    ShowIconSelectorPopup = (_, _, _) => { },
                    AttachDropdownWithinWindow = _ => { },
                    BrowseFile = (_, _) => null,
                    BrowseFolder = _ => null,
                };

                InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

                var sensorCombo = VisualTree.FindDescendants<ComboBox>(target)
                    .FirstOrDefault(c => c.IsEnabled
                        && c.ItemsSource is IEnumerable<WidgetPropertyOption> opts
                        && opts.Any(o => o.DisplayName == "Automatic (first available sensor)"));
                Assert.IsNotNull(sensorCombo,
                    "HWiNFO settings must still present an active Sensor dropdown while waiting for the vendor list");
                Assert.IsTrue(
                    VisualTree.FindDescendants<TextBlock>(target).Any(t => t.Text.Contains("No HWiNFO sensors available yet", StringComparison.Ordinal)),
                    "the hint beneath the dropdown must still explain why no live sensors appear yet");
            }
            finally
            {
                ModernWigiDash.Hardware.Service.VendorService.SetInstance(null);
            }
        });
    }

    [TestMethod]
    public void Render_HwinfoOfflinePersistedKey_PreservesKeyDropdownAndHint()
    {
        StaRunner.Run(() =>
        {
            ModernWigiDash.Hardware.Service.VendorService.SetInstance(null);
            try
            {
                var widget = new HwinfoWidget { SensorKey = "6:12:3" };
                var placed = new PlacedWidgetInstance { PluginId = "hwinfo", DisplayName = "HWiNFO Sensor", ActiveInstance = widget };
                var target = new StackPanel();
                var callbacks = new InspectorCallbacks
                {
                    TryFindResource = _ => null,
                    ApplyInspectorPropertyValue = (_, _) => { },
                    ShowIconSelectorPopup = (_, _, _) => { },
                    AttachDropdownWithinWindow = _ => { },
                    BrowseFile = (_, _) => null,
                    BrowseFolder = _ => null,
                };

                InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

                var sensorCombo = VisualTree.FindDescendants<ComboBox>(target)
                    .FirstOrDefault(c => c.IsEnabled
                        && c.ItemsSource is IEnumerable<WidgetPropertyOption> opts
                        && opts.Any(o => o.Value == "6:12:3"));
                Assert.IsNotNull(sensorCombo, "offline inspector must still display the stored HWiNFO key in the dropdown");
                Assert.IsTrue(
                    VisualTree.FindDescendants<TextBlock>(target).Any(t => t.Text.Contains("No HWiNFO sensors available yet", StringComparison.Ordinal)),
                    "offline persisted-key inspector must keep the status hint");
            }
            finally
            {
                ModernWigiDash.Hardware.Service.VendorService.SetInstance(null);
            }
        });
    }

    [TestMethod]
    public void Render_HwinfoLiveSensors_HidesWaitingStatusHint()
    {
        StaRunner.Run(() =>
        {
            using var client = new ModernWigiDash.Hardware.Service.WigiDashServiceClient(new StaticSensorValues());
            ModernWigiDash.Hardware.Service.VendorService.SetInstance(client);
            try
            {
                var widget = new HwinfoWidget();
                var placed = new PlacedWidgetInstance { PluginId = "hwinfo", DisplayName = "HWiNFO Sensor", ActiveInstance = widget };
                var target = new StackPanel();
                var callbacks = new InspectorCallbacks
                {
                    TryFindResource = _ => null,
                    ApplyInspectorPropertyValue = (_, _) => { },
                    ShowIconSelectorPopup = (_, _, _) => { },
                    AttachDropdownWithinWindow = _ => { },
                    BrowseFile = (_, _) => null,
                    BrowseFolder = _ => null,
                };

                InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

                var sensorCombo = VisualTree.FindDescendants<ComboBox>(target)
                    .FirstOrDefault(c => c.IsEnabled
                        && c.ItemsSource is IEnumerable<WidgetPropertyOption> opts
                        && opts.Any(o => o.Value == "0:0:0" && o.DisplayName == "CPU Temp (CPU)"));
                Assert.IsNotNull(sensorCombo, "live sensors must populate the Sensor dropdown");
                Assert.IsFalse(
                    VisualTree.FindDescendants<TextBlock>(target).Any(t => t.Text.Contains("No HWiNFO sensors available yet", StringComparison.Ordinal)),
                    "waiting hint must disappear once live sensors are present");
            }
            finally
            {
                ModernWigiDash.Hardware.Service.VendorService.SetInstance(null);
            }
        });
    }

    [TestMethod]
    public void Render_RequiredChoiceWithFallbackOnlyAndNoDescription_ShowsDefaultHint()
    {
        StaRunner.Run(() =>
        {
            var widget = new UndescribedFallbackChoiceWidget();
            var placed = new PlacedWidgetInstance { PluginId = "hwinfo", DisplayName = "HWiNFO", ActiveInstance = widget };
            var target = new StackPanel();
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            Assert.IsTrue(
                VisualTree.FindDescendants<TextBlock>(target).Any(t =>
                    t.Text == "No options available yet. Connect the data source, then select this widget again."),
                "fallback-only required Choice with no Description must still show the default unavailable-options hint");
        });
    }

    [TestMethod]
    public void Render_PlainChoiceWithoutOptions_StaysAFreeTextEditor()
    {
        // Twitch's channel box relies on free text while the followed-channels
        // list is empty. Only a required list may replace the text box, so the
        // arm order (options > 0, then RequiredListedValue) is pinned here.
        StaRunner.Run(() =>
        {
            var widget = new PlainChoiceWidget();
            var placed = new PlacedWidgetInstance { PluginId = "twitch", DisplayName = "Twitch", ActiveInstance = widget };
            var target = new StackPanel();
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            Assert.AreEqual(1, VisualTree.FindDescendants<TextBox>(target).Count(),
                "a plain Choice without options keeps its free-text editor");
            Assert.IsFalse(
                VisualTree.FindDescendants<TextBlock>(target).Any(t => t.Text == PlainChoiceWidget.Hint),
                "a plain Choice must not show the required-list empty state");
        });
    }

    private sealed class PlainChoiceWidget : ModernWidgetBase
    {
        public const string Hint = "Type a channel name.";

        [WidgetProperty("Channel", WidgetPropertyType.Choice, Hint)]
        public string Channel { get; set; } = "";

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }

    [TestMethod]
    public void Render_SensorSelectorWithoutOptions_KeepsTheServiceInstruction()
    {
        // The shared empty-state builder must not replace the store-backed
        // selector's actionable text with its generic property description.
        StaRunner.Run(() =>
        {
            var widget = new SensorSelectorWidget();
            var placed = new PlacedWidgetInstance { PluginId = "hardware_monitor", DisplayName = "Hardware Monitor", ActiveInstance = widget };
            var target = new StackPanel();
            var callbacks = new InspectorCallbacks
            {
                TryFindResource = _ => null,
                ApplyInspectorPropertyValue = (_, _) => { },
                ShowIconSelectorPopup = (_, _, _) => { },
                AttachDropdownWithinWindow = _ => { },
                BrowseFile = (_, _) => null,
                BrowseFolder = _ => null,
            };

            InspectorPanelRenderer.Render(placed, InspectorModelBuilder.Describe(placed, []), target.Children, callbacks);

            Assert.IsTrue(
                VisualTree.FindDescendants<TextBlock>(target).Any(t => t.Text.Contains("Start LibreHardwareService", StringComparison.Ordinal)),
                "the selector's empty state must keep the service instruction");
        });
    }

    private sealed class SensorSelectorWidget : ModernWidgetBase
    {
        [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector, "Select a live sensor reading from LibreHardwareService")]
        public string SensorLabel { get; set; } = "";

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }

    private sealed class RequiredChoiceWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
    {
        public const string Hint = "Start the vendor service, then reopen settings.";

        [WidgetProperty("Sensor", WidgetPropertyType.Choice, Hint, "", RequiresListedValue = true)]
        public string SensorKey { get; set; } = "";

        public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName) => [];

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }

    private sealed class UndescribedFallbackChoiceWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
    {
        [WidgetProperty("Sensor", WidgetPropertyType.Choice, "", "", RequiresListedValue = true)]
        public string SensorKey { get; set; } = "";

        public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
            => [new("", "Automatic") { IsFallback = true }];

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }

    private sealed class StaticSensorValues : ModernWigiDash.Hardware.Service.ISensorValues
    {
        public bool IsReady => true;

        public IReadOnlyList<ModernWigiDash.Hardware.Service.VendorSensorItem>? GetSensorList()
            => [new ModernWigiDash.Hardware.Service.VendorSensorItem(Guid.NewGuid(), "CPU Temp", 0, 0, 0, "CPU", "C")];

        public ModernWigiDash.Hardware.Service.VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2)
            => new(45.0, true);
    }

    private sealed class BooleanWidget : ModernWidgetBase
    {
        [WidgetProperty("Flag", WidgetPropertyType.Boolean, defaultValue: true)]
        public bool Flag { get; set; } = true;

        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
    }
}
