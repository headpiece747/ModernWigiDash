using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.App.Controls;
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

    /// <summary>Opens the icon picker for a property with an <see cref="EditorKind.IconPicker"/> editor.</summary>
    public required Action<PropertyInfo, IWidgetEditorProvider, TextBox> ShowIconSelectorPopup { get; init; }

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
/// Widgets that need special editors implement <see cref="IWidgetEditorProvider"/>
/// (icon pickers, action-command paths); the renderer never branches on widget types.
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
        var provider = widget.ActiveInstance as IWidgetEditorProvider;

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
                    var combo = BuildOptionCombo(desc.Options, desc, isUpdatingInspector, callbacks);
                    if (provider?.ActionCommandVisibilityChoicePropertyName == desc.Property.Name) actionTypeCombo = combo;
                    propPanel.Children.Add(combo);
                    break;
                case WidgetPropertyType.Font:
                    IReadOnlyList<WidgetPropertyOption> fontOptions = desc.Options.Count > 0
                    ? desc.Options
                    : FontHelper.GetAllFamilies().Select(family => new WidgetPropertyOption(family, family)).ToArray();
                    propPanel.Children.Add(BuildOptionCombo(fontOptions, desc, isUpdatingInspector, callbacks));
                    break;
                case WidgetPropertyType.Icon:
                    propPanel.Children.Add(BuildIconEditor(widget, desc, isUpdatingInspector, callbacks));
                    break;
                case WidgetPropertyType.Boolean:
                    propPanel.Children.Add(BuildBooleanEditor(desc, callbacks));
                    break;
                case WidgetPropertyType.Color:
                    propPanel.Children.Add(BuildColorEditor(desc, isUpdatingInspector, callbacks));
                    break;
                case WidgetPropertyType.Path:
                    if (provider?.GetEditorKind(desc.Property) == EditorKind.ActionCommand)
                    {
                        propPanel.Children.Add(BuildPathEditor(desc, isUpdatingInspector, callbacks,
                            "Select action folder", "Select action file or executable", "Programs and files (*.*)|*.*"));
                        actionCommandPanel = propPanel;
                    }
                    else
                    {
                        propPanel.Children.Add(BuildPathEditor(desc, isUpdatingInspector, callbacks,
                            "Select image folder", "Select image file", "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"));
                    }
                    break;
                case WidgetPropertyType.SensorSelector:
                    propPanel.Children.Add(BuildSensorSelector(desc, isUpdatingInspector, callbacks));
                    break;
                default:
                    // Text or Number
                    propPanel.Children.Add(BuildTextEditor(desc, isUpdatingInspector, callbacks));
                    break;
            }

            target.Add(propPanel);
        }

        if (actionTypeCombo != null && actionCommandPanel != null && provider != null)
        {
            void UpdateActionCommandVisibility()
            {
                string? selected = actionTypeCombo.SelectedValue?.ToString();
                actionCommandPanel.Visibility = provider.IsActionCommandVisible(selected)
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

    /// <summary>
    /// One option-combo builder for every choice-style editor: the choice,
    /// font, and sensor selectors all share this shape (ItemsSource /
    /// DisplayMemberPath / SelectedValuePath / guarded write-back / dropdown
    /// clamp). The three call sites only differ in the option source.
    /// </summary>
    private static ComboBox BuildOptionCombo(IReadOnlyList<WidgetPropertyOption> options, EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
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
            if (isUpdatingInspector()) return;
            string? selectedValue = combo.SelectedValue?.ToString();
            // Empty values are valid selections (e.g. the weather widget's
            // "Automatic (by ranking)" Location Match entry) — only a null
            // selection (nothing chosen) is skipped.
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
    private static UIElement BuildIconEditor(PlacedWidgetInstance widget, EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
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
            if (isUpdatingInspector()) return;
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
    private static UIElement BuildPathEditor(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks, string folderTitle, string fileTitle, string fileFilter)
    {
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

    private static UIElement BuildSensorSelector(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
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

        return BuildOptionCombo(desc.Options, desc, isUpdatingInspector, callbacks);
    }

    private static UIElement BuildColorEditor(EditorDescription desc, Func<bool> isUpdatingInspector, InspectorCallbacks callbacks)
    {
        var editor = new ColorPickerEditor
        {
            Hex = desc.CurrentValue?.ToString() ?? ""
        };
        editor.Applied += hex =>
        {
            if (isUpdatingInspector()) return;
            callbacks.ApplyInspectorPropertyValue(desc.Property, hex);
        };
        return editor;
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
