using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Callbacks the inspector renderer needs from its host window. Every value
/// write-back funnels through <see cref="ApplyInspectorPropertyValue"/> — the
/// renderer never touches the widget model directly. Dialogs are host-side
/// (the renderer is dialog-free); <c>isUpdatingInspector</c> is a live guard
/// that suppresses change events while the panel is rebuilt.
/// </summary>
public sealed class InspectorCallbacks
{
    /// <summary>Resolves a named resource from the window/application (may return null).</summary>
    public required Func<string, object?> TryFindResource { get; init; }

    /// <summary>Writes an inspector value back into the widget and profile (host-side side effect).</summary>
    public required Action<PropertyInfo?, object> ApplyInspectorPropertyValue { get; init; }

    /// <summary>Opens the icon picker for a hotkey button widget.</summary>
    public required Action<PropertyInfo, HotkeyButtonWidget, TextBox> ShowIconSelectorPopup { get; init; }

    /// <summary>Keeps a ComboBox dropdown inside the window's client area.</summary>
    public required Action<ComboBox> AttachDropdownWithinWindow { get; init; }

    /// <summary>Opens a file picker; returns the chosen path or null when cancelled.</summary>
    public required Func<string, string?, string?> BrowseFile { get; init; }

    /// <summary>Opens a folder picker; returns the chosen path or null when cancelled.</summary>
    public required Func<string, string?> BrowseFolder { get; init; }
}

/// <summary>
/// Thin WPF mapper: renders <see cref="EditorDescription"/>s (produced by the
/// pure <see cref="InspectorModelBuilder"/>) into editor controls. All value
/// changes call <see cref="InspectorCallbacks.ApplyInspectorPropertyValue"/>
/// — one seam for every property type — and dialogs live behind the callbacks.
/// </summary>
public static class InspectorPanelRenderer
{
    /// <summary>
    /// Renders editors for every <see cref="EditorDescription"/> into
    /// <paramref name="target"/> (cleared by the caller).
    /// </summary>
    /// <param name="widget">The placed widget being inspected; action buttons
    /// and the icon picker resolve its active instance at interaction time.</param>
    public static void Render(
        PlacedWidgetInstance widget,
        IReadOnlyList<EditorDescription> descriptions,
        UIElementCollection target,
        Func<bool> isUpdatingInspector,
        InspectorCallbacks callbacks)
    {
        ComboBox? actionTypeCombo = null;
        StackPanel? actionCommandPanel = null;

        foreach (var desc in descriptions)
        {
            if (desc.IsAction)
            {
                target.Add(BuildActionButton(widget, desc, callbacks));
                continue;
            }

            var propPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            propPanel.Children.Add(new TextBlock
            {
                Text = desc.DisplayName,
                FontSize = 11,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });

            switch (desc.PropertyType)
            {
                case WidgetPropertyType.Choice when desc.Options.Count > 0:
                    var combo = BuildChoiceCombo(desc, isUpdatingInspector, callbacks);
                    if (desc.Property.Name == nameof(HotkeyButtonWidget.ActionType)) actionTypeCombo = combo;
                    propPanel.Children.Add(combo);
                    break;
                case WidgetPropertyType.Font:
                    propPanel.Children.Add(BuildFontCombo(desc, isUpdatingInspector, callbacks));
                    break;
                case WidgetPropertyType.Icon:
                    propPanel.Children.Add(BuildIconEditor(widget, desc, isUpdatingInspector, callbacks));
                    break;
                case WidgetPropertyType.Boolean:
                    propPanel.Children.Add(BuildBooleanEditor(desc, callbacks));
                    break;
                case WidgetPropertyType.Path:
                    propPanel.Children.Add(BuildPathEditor(desc, isUpdatingInspector, callbacks));
                    if (desc.Property.Name == nameof(HotkeyButtonWidget.ActionCommand)) actionCommandPanel = propPanel;
                    break;
                case WidgetPropertyType.SensorSelector:
                    propPanel.Children.Add(BuildSensorSelector(desc, isUpdatingInspector, callbacks));
                    break;
                default:
                    // Text, Number, or Color
                    propPanel.Children.Add(BuildTextEditor(desc, isUpdatingInspector, callbacks));
                    break;
            }

            target.Add(propPanel);
        }

        if (actionTypeCombo != null && actionCommandPanel != null)
        {
            void UpdateActionCommandVisibility()
            {
                string? selected = actionTypeCombo.SelectedValue?.ToString();
                actionCommandPanel.Visibility =
                    selected != null && HotkeyButtonWidget.IsLaunchOrUrlAction(selected)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            actionTypeCombo.SelectionChanged += (_, _) => UpdateActionCommandVisibility();
            UpdateActionCommandVisibility();
        }
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

    private static ComboBox BuildChoiceCombo(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        var combo = new ComboBox
        {
            ItemsSource = desc.Options,
            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
            SelectedValuePath = nameof(WidgetPropertyOption.Value),
            SelectedValue = desc.CurrentValue?.ToString(),
            Padding = new Thickness(8, 4, 8, 4)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (isUpdatingInspector()) return;
            string? selectedValue = combo.SelectedValue?.ToString();
            if (!string.IsNullOrWhiteSpace(selectedValue))
                callbacks.ApplyInspectorPropertyValue(desc.Property, selectedValue);
        };
        callbacks.AttachDropdownWithinWindow(combo);
        return combo;
    }

    private static ComboBox BuildFontCombo(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        var combo = new ComboBox
        {
            ItemsSource = desc.Options.Count > 0
                ? desc.Options
                : FontCatalog.GetAllFamilies().Select(family => new WidgetPropertyOption(family, family)).ToArray(),
            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
            SelectedValuePath = nameof(WidgetPropertyOption.Value),
            SelectedValue = desc.CurrentValue?.ToString(),
            Padding = new Thickness(8, 4, 8, 4)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (isUpdatingInspector()) return;
            string? selectedValue = combo.SelectedValue?.ToString();
            if (!string.IsNullOrWhiteSpace(selectedValue))
                callbacks.ApplyInspectorPropertyValue(desc.Property, selectedValue);
        };
        callbacks.AttachDropdownWithinWindow(combo);
        return combo;
    }

    private static UIElement BuildIconEditor(PlacedWidgetInstance widget, EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        PropertyInfo? iconFileProp = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile));
        string seed = desc.CurrentValue?.ToString() ?? "";
        if (string.IsNullOrEmpty(seed)
            && widget.ActiveInstance is HotkeyButtonWidget hotkeySeed
            && !string.IsNullOrEmpty(hotkeySeed.IconFile))
        {
            seed = hotkeySeed.IconFile;
        }
        var box = new TextBox { Text = seed };
        var btnBrowse = new Button { Content = "Browse\u2026", Padding = new Thickness(8, 2, 8, 2) };
        DockPanel.SetDock(btnBrowse, Dock.Right);
        btnBrowse.Click += (_, _) =>
        {
            if (widget.ActiveInstance is not HotkeyButtonWidget hotkey) return;
            callbacks.ShowIconSelectorPopup(desc.Property, hotkey, box);
        };
        box.TextChanged += (_, _) =>
        {
            if (isUpdatingInspector()) return;
            callbacks.ApplyInspectorPropertyValue(iconFileProp, "");
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
            Content = "Enabled / Active",
            IsChecked = desc.CurrentValue is bool b && b,
            Foreground = Brushes.White
        };
        chk.Checked += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, true);
        chk.Unchecked += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, false);
        return chk;
    }

    private static UIElement BuildPathEditor(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        bool isHotkeyCommand = desc.Property.DeclaringType == typeof(HotkeyButtonWidget) &&
            desc.Property.Name == nameof(HotkeyButtonWidget.ActionCommand);

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var txt = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
        txt.TextChanged += (_, _) =>
        {
            if (isUpdatingInspector()) return;
            callbacks.ApplyInspectorPropertyValue(desc.Property, txt.Text);
        };

        var btnFolder = new Button
        {
            Content = "Folder\u2026",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        DockPanel.SetDock(btnFolder, Dock.Right);
        btnFolder.Click += (_, _) =>
        {
            string? folder = callbacks.BrowseFolder(isHotkeyCommand ? "Select action folder" : "Select image folder");
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
            string? file = callbacks.BrowseFile(
                isHotkeyCommand ? "Select action file or executable" : "Select image file",
                isHotkeyCommand
                    ? "Programs and files (*.*)|*.*"
                    : "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*");
            if (!string.IsNullOrEmpty(file)) txt.Text = file;
        };

        row.Children.Add(btnFile);
        row.Children.Add(btnFolder);
        row.Children.Add(txt);
        return row;
    }

    private static UIElement BuildSensorSelector(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        if (desc.Options.Count == 0)
        {
            return new TextBlock
            {
                Text = "No sensors detected. Start the ModernWigiDash service, then reopen settings to pick a sensor.",
                FontSize = 11,
                Foreground = callbacks.TryFindResource("TextSecondary") as Brush ?? Brushes.White,
                TextWrapping = TextWrapping.Wrap
            };
        }

        var combo = new ComboBox
        {
            ItemsSource = desc.Options,
            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
            SelectedValuePath = nameof(WidgetPropertyOption.Value),
            SelectedValue = desc.CurrentValue?.ToString(),
            Padding = new Thickness(8, 4, 8, 4)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (isUpdatingInspector()) return;
            string? selectedValue = combo.SelectedValue?.ToString();
            if (!string.IsNullOrWhiteSpace(selectedValue))
                callbacks.ApplyInspectorPropertyValue(desc.Property, selectedValue);
        };
        callbacks.AttachDropdownWithinWindow(combo);
        return combo;
    }

    private static TextBox BuildTextEditor(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        var txt = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
        txt.TextChanged += (_, _) =>
        {
            if (isUpdatingInspector()) return;
            callbacks.ApplyInspectorPropertyValue(desc.Property, txt.Text);
        };
        return txt;
    }
}
