using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Callbacks the inspector renderer needs from its host window. Every value
/// write-back funnels through <see cref="ApplyInspectorPropertyValue"/> — the
/// renderer never touches the widget model directly. Dialogs are host-side
/// (the renderer is dialog-free); the re-entrancy guard (suppression of
/// programmatic sets during a panel rebuild) is enforced at that funnel, so
/// no editor builder carries its own suppression check.
/// </summary>
internal sealed class InspectorCallbacks
{
    /// <summary>Resolves a named resource from the window/application (may return null).</summary>
    public required Func<string, object?> TryFindResource { get; init; }

    /// <summary>Writes an inspector value back into the widget and profile (host-side side effect).</summary>
    public required Action<PropertyInfo?, object> ApplyInspectorPropertyValue { get; init; }

    /// <summary>Opens the icon picker for a property with an <see cref="EditorKind.IconPicker"/> editor.</summary>
    public required Action<PropertyInfo, IWidgetEditorProvider, TextBox> ShowIconSelectorPopup { get; init; }

    /// <summary>Keeps a ComboBox dropdown inside the window's client area.</summary>
    public required Action<ComboBox> AttachDropdownWithinWindow { get; init; }

    /// <summary>Opens a file picker; returns the chosen path or null when cancelled.</summary>
    public required Func<string, string?, string?> BrowseFile { get; init; }

    /// <summary>Opens a folder picker; returns the chosen path or null when cancelled.</summary>
    public required Func<string, string?> BrowseFolder { get; init; }

    /// <summary>Commits a location-search pick (the picked label) to the
    /// selected widget through its IWidgetLocationSearch contract.</summary>
    public Action<GeocodeCandidate>? CommitLocationPick { get; init; }

    /// <summary>Saves a machine-local CalDAV password for a calendar feed (the
    /// feed editor's credential field routes here; the host writes through its
    /// DPAPI-backed store, never the profile).</summary>
    public Action<string, string>? SaveCalendarCredential { get; init; }
}

/// <summary>
/// Thin WPF mapper: renders <see cref="EditorDescription"/>s (produced by the
/// pure <see cref="InspectorModelBuilder"/>) into editor controls. All value
/// changes call <see cref="InspectorCallbacks.ApplyInspectorPropertyValue"/>
/// — one seam for every property type — and dialogs live behind the callbacks.
/// Widgets that need special editors implement <see cref="IWidgetEditorProvider"/>
/// (icon pickers, action-command paths); the renderer delegates custom editors
/// to the pluggable <see cref="InspectorEditorRegistry"/>.
/// </summary>
internal static class InspectorPanelRenderer
{
    /// <summary>
    /// Renders editors for every <see cref="EditorDescription"/> into
    /// <paramref name="target"/> (cleared by the caller).
    /// </summary>
    /// <param name="widget">The placed widget being inspected; action buttons
    /// and the icon picker resolve its active instance at interaction time.</param>
    /// <param name="descriptions">The editor descriptions to render, one per
    /// property or action (the pure model's output).</param>
    /// <param name="target">The element collection the editors render into
    /// (cleared by the caller).</param>
    /// <param name="callbacks">The host callbacks the editors and dialogs
    /// route through.</param>
    public static void Render(
        PlacedWidgetInstance widget,
        IReadOnlyList<EditorDescription> descriptions,
        UIElementCollection target,
        InspectorCallbacks callbacks)
    {
        var wiring = new ActionCommandWiring
        {
            Provider = widget.ActiveInstance as IWidgetEditorProvider
        };

        foreach (var desc in descriptions)
        {
            if (desc.IsAction)
            {
                target.Add(BuildActionButton(widget, desc, callbacks));
                continue;
            }

            AddPropertyPanel(widget, desc, target, callbacks, wiring);
        }

        WireActionCommandVisibility(wiring);
    }

    /// <summary>
    /// The action-command wiring state accumulated while building property
    /// editors: the action-type combo (when the widget names it), the
    /// action-command path panel (when the widget marks it), and the provider
    /// that decides whether the panel shows. <see cref="Render"/> wires the
    /// panel's visibility to the combo once both halves exist.
    /// </summary>
    private sealed class ActionCommandWiring
    {
        public required IWidgetEditorProvider? Provider { get; init; }
        public ComboBox? TypeCombo { get; set; }
        public StackPanel? CommandPanel { get; set; }
    }

    /// <summary>
    /// Builds one property's editor panel (title + the type-specific editor)
    /// and adds it to <paramref name="target"/>. Records the action-command
    /// wiring halves it produces in <paramref name="wiring"/>.
    /// </summary>
    private static void AddPropertyPanel(PlacedWidgetInstance widget, EditorDescription desc, UIElementCollection target, InspectorCallbacks callbacks, ActionCommandWiring wiring)
    {
        var propPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        propPanel.Children.Add(new TextBlock
        {
            Text = desc.DisplayName,
            FontSize = 11,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4)
        });

        BuildPropertyEditor(widget, desc, propPanel, callbacks, wiring);
        target.Add(propPanel);
    }

    /// <summary>
    /// Adds the type-specific editor for one property description into
    /// <paramref name="panel"/>. The choice and path branches record the
    /// action-command wiring halves in <paramref name="wiring"/>.
    /// </summary>
    private static void BuildPropertyEditor(PlacedWidgetInstance widget, EditorDescription desc, StackPanel panel, InspectorCallbacks callbacks, ActionCommandWiring wiring)
    {
        var provider = wiring.Provider;
        switch (desc.PropertyType)
        {
            case WidgetPropertyType.Choice when desc.Options.Count > 0:
                var combo = BuildOptionCombo(desc.Options, desc, callbacks);
                if (string.Equals(provider?.ActionCommandVisibilityChoicePropertyName, desc.Property.Name, StringComparison.Ordinal)) wiring.TypeCombo = combo;
                panel.Children.Add(combo);
                break;
            case WidgetPropertyType.Font:
                IReadOnlyList<WidgetPropertyOption> fontOptions = desc.Options.Count > 0
                ? desc.Options
                : FontHelper.GetAllFamilies().Select(family => new WidgetPropertyOption(family, family)).ToArray();
                panel.Children.Add(BuildOptionCombo(fontOptions, desc, callbacks));
                break;
            case WidgetPropertyType.Icon:
                panel.Children.Add(BuildIconEditor(widget, desc, callbacks));
                break;
            case WidgetPropertyType.Boolean:
                panel.Children.Add(BuildBooleanEditor(desc, callbacks));
                break;
            case WidgetPropertyType.Color:
                panel.Children.Add(BuildColorEditor(desc, callbacks));
                break;
            case WidgetPropertyType.Path:
                if (provider?.GetEditorKind(desc.Property) == EditorKind.ActionCommand)
                {
                    panel.Children.Add(BuildPathEditor(desc, callbacks,
                        "Select action folder", "Select action file or executable", "Programs and files (*.*)|*.*"));
                    wiring.CommandPanel = panel;
                }
                else
                {
                    panel.Children.Add(BuildPathEditor(desc, callbacks,
                        "Select image folder", "Select image file", "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"));
                }
                break;
            case WidgetPropertyType.SensorSelector:
                panel.Children.Add(BuildSensorSelector(desc, callbacks));
                break;
            case WidgetPropertyType.Text when provider?.GetEditorKind(desc.Property) is { } kind && InspectorEditorRegistry.Default.TryGetEditor(kind, out var customEditor):
                panel.Children.Add(customEditor!.BuildEditor(widget, desc, callbacks));
                break;
            default:
                // Text or Number
                panel.Children.Add(BuildTextEditor(desc, callbacks));
                break;
        }
    }

    /// <summary>
    /// Wires the action-command panel's visibility to the action-type combo
    /// once both halves exist (a widget that names its action-type choice and
    /// marks its command path): the panel shows or hides as the chosen action
    /// kind toggles.
    /// </summary>
    private static void WireActionCommandVisibility(ActionCommandWiring wiring)
    {
        if (wiring.TypeCombo is not { } combo || wiring.CommandPanel is not { } panel || wiring.Provider is not { } provider)
            return;

        void UpdateActionCommandVisibility()
        {
            string? selected = combo.SelectedValue?.ToString();
            panel.Visibility = provider.IsActionCommandVisible(selected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        combo.SelectionChanged += (_, _) => UpdateActionCommandVisibility();
        UpdateActionCommandVisibility();
    }

    private static Button BuildActionButton(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var btn = new Button
        {
            Content = desc.DisplayName,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var instance = widget.ActiveInstance;
        if (instance is IWidgetActionPresentationProvider presentation)
        {
            string? label = presentation.GetWidgetActionLabel(desc.Property.Name);
            if (!string.IsNullOrWhiteSpace(label)) btn.Content = label;

            if (presentation.IsWidgetActionActive(desc.Property.Name))
            {
                btn.Background = callbacks.TryFindResource("SuccessBackground") as Brush ?? btn.Background;
                btn.BorderBrush = callbacks.TryFindResource("SuccessBorder") as Brush ?? btn.BorderBrush;
                btn.BorderThickness = new Thickness(1);
                btn.Foreground = callbacks.TryFindResource("M3Primary") as Brush ?? Brushes.White;
            }
        }

        btn.Click += (_, _) =>
        {
            if (widget.ActiveInstance is IWidgetActionInvoker invoker)
                invoker.InvokeWidgetAction(desc.Property.Name);
        };
        return btn;
    }

    /// <summary>
    /// One option-combo builder for every choice-style editor: the choice,
    /// font, and sensor selectors all share this shape (ItemsSource /
    /// DisplayMemberPath / SelectedValuePath / funnel-routed write-back /
    /// dropdown clamp). The three call sites only differ in the option source.
    /// </summary>
    private static ComboBox BuildOptionCombo(IReadOnlyList<WidgetPropertyOption> options, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
            SelectedValuePath = nameof(WidgetPropertyOption.Value),
            SelectedValue = desc.CurrentValue?.ToString(),
            Padding = new Thickness(8, 4, 8, 4)
        };
        combo.SelectionChanged += (_, _) =>
        {
            string? selectedValue = combo.SelectedValue?.ToString();
            if (selectedValue is not null)
                callbacks.ApplyInspectorPropertyValue(desc.Property, selectedValue);
        };
        callbacks.AttachDropdownWithinWindow(combo);
        return combo;
    }

    /// <summary>
    /// Icon picker row. The widget's <see cref="IWidgetEditorProvider"/> supplies
    /// the companion file-path property (cleared whenever the named icon changes)
    /// and the popup host; a widget without a provider gets a plain text editor.
    /// </summary>
    private static UIElement BuildIconEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var provider = widget.ActiveInstance as IWidgetEditorProvider;
        PropertyInfo? iconFileProp = provider?.GetIconFileCompanion(desc.Property);
        string seed = desc.CurrentValue?.ToString() ?? "";
        if (string.IsNullOrEmpty(seed)
            && iconFileProp != null
            && widget.ActiveInstance != null
            && iconFileProp.GetValue(widget.ActiveInstance) is string companionSeed
            && !string.IsNullOrEmpty(companionSeed))
        {
            seed = companionSeed;
        }
        var box = new TextBox { Text = seed };
        var btnBrowse = new Button { Content = "Browse\u2026", Padding = new Thickness(8, 2, 8, 2) };
        DockPanel.SetDock(btnBrowse, Dock.Right);
        btnBrowse.Click += (_, _) =>
        {
            if (provider == null) return;
            callbacks.ShowIconSelectorPopup(desc.Property, provider, box);
        };
        box.TextChanged += (_, _) =>
        {
            if (iconFileProp != null) callbacks.ApplyInspectorPropertyValue(iconFileProp, "");
            callbacks.ApplyInspectorPropertyValue(desc.Property, box.Text);
        };
        row.Children.Add(btnBrowse);
        row.Children.Add(box);
        return row;
    }

    private static CheckBox BuildBooleanEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var chk = new CheckBox
        {
            Content = desc.DisplayName,
            IsChecked = desc.CurrentValue is bool b && b,
            Foreground = Brushes.White
        };
        chk.Checked += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, true);
        chk.Unchecked += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, false);
        return chk;
    }

    /// <summary>Path editor: text box with Folder/File pickers. The action
    /// command and image-path editors differ only in the dialog title and file
    /// filter — one parameterized builder instead of two ~40-line copies.</summary>
    private static UIElement BuildPathEditor(EditorDescription desc, InspectorCallbacks callbacks, string folderTitle, string fileTitle, string fileFilter)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var txt = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
        txt.TextChanged += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, txt.Text);

        var btnFolder = new Button
        {
            Content = "Folder\u2026",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        DockPanel.SetDock(btnFolder, Dock.Right);
        btnFolder.Click += (_, _) =>
        {
            string? folder = callbacks.BrowseFolder(folderTitle);
            if (!string.IsNullOrEmpty(folder)) txt.Text = folder;
        };

        var btnFile = new Button
        {
            Content = "File\u2026",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        DockPanel.SetDock(btnFile, Dock.Right);
        btnFile.Click += (_, _) =>
        {
            string? file = callbacks.BrowseFile(fileTitle, fileFilter);
            if (!string.IsNullOrEmpty(file)) txt.Text = file;
        };

        row.Children.Add(btnFile);
        row.Children.Add(btnFolder);
        row.Children.Add(txt);
        return row;
    }

    private static UIElement BuildSensorSelector(EditorDescription desc, InspectorCallbacks callbacks)
    {
        if (desc.Options.Count == 0)
        {
            return new TextBlock
            {
                Text = "No sensors detected. Start LibreHardwareService, then reopen settings to pick a sensor.",
                FontSize = 11,
                Foreground = callbacks.TryFindResource("TextSecondary") as Brush ?? Brushes.White,
                TextWrapping = TextWrapping.Wrap
            };
        }

        return BuildOptionCombo(desc.Options, desc, callbacks);
    }

    private static UIElement BuildColorEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var editor = new ColorPickerEditor
        {
            Hex = desc.CurrentValue?.ToString() ?? ""
        };
        editor.Applied += hex => callbacks.ApplyInspectorPropertyValue(desc.Property, hex);
        return editor;
    }

    private static TextBox BuildTextEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var txt = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
        txt.TextChanged += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, txt.Text);
        return txt;
    }

    // ── Preserved Test Seams ─────────────────────────────────

    /// <summary>
    /// Builds the key capture editor tuple (Editor element and underlying model).
    /// Preserved test seam delegating to <see cref="KeyCapturePropertyEditor"/>.
    /// </summary>
    internal static (UIElement Editor, KeyCaptureModel Model) BuildKeyCaptureEditor(EditorDescription desc, InspectorCallbacks callbacks)
        => KeyCapturePropertyEditor.BuildKeyCaptureEditor(desc, callbacks);

    /// <summary>
    /// Applies one tick decision to the results list and popup.
    /// Preserved test seam delegating to <see cref="LocationSearchPropertyEditor"/>.
    /// </summary>
    internal static void ApplySearchResults(ListBox results, Popup popup, LocationSearchTick outcome, IReadOnlyList<GeocodeCandidate>? candidates)
        => LocationSearchPropertyEditor.ApplySearchResults(results, popup, outcome, candidates);

    /// <summary>
    /// Formats one search result line with label and compact population suffix.
    /// Preserved test seam delegating to <see cref="LocationSearchPropertyEditor.CandidateLineConverter"/>.
    /// </summary>
    internal sealed class CandidateLineConverter : IMultiValueConverter
    {
        private readonly LocationSearchPropertyEditor.CandidateLineConverter _inner = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
            => _inner.Convert(values, targetType, parameter, culture);

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => _inner.ConvertBack(value, targetTypes, parameter, culture);
    }
}
