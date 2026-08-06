using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Callbacks the inspector builder needs from its host window.
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
}

/// <summary>
/// Builds the dynamic custom-property editor controls for a widget from its
/// <see cref="WidgetPropertyAttribute"/>-annotated properties. Extracted from
/// MainWindow so the reflection→UI mapping lives in one testable module.
/// </summary>
public static class WidgetInspectorPanelBuilder
{
    /// <summary>
    /// Populates <paramref name="target"/> with editors for every
    /// <c>[WidgetProperty]</c> property on the widget instance.
    /// </summary>
    /// <param name="widget">The placed widget being inspected (must have an active instance).</param>
    /// <param name="target">The panel to populate (cleared by the caller).</param>
    /// <param name="isUpdatingInspector">Suppresses change events while the inspector is being rebuilt.</param>
    public static void BuildCustomPropertyEditors(PlacedWidgetInstance widget, UIElementCollection target, bool isUpdatingInspector, InspectorCallbacks callbacks)
    {
        var instance = widget.ActiveInstance;
        if (instance == null) return;

        var type = instance.GetType();
        ComboBox? actionTypeCombo = null;
        StackPanel? actionCommandPanel = null;

        foreach (var prop in type.GetProperties())
        {
            var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
            if (attr == null) continue;

            if (prop.DeclaringType == typeof(HotkeyButtonWidget) &&
                prop.Name == nameof(HotkeyButtonWidget.IconFile))
                continue;

            if (attr.PropertyType == WidgetPropertyType.Button)
            {
                var btn = new Button
                {
                    Content = attr.DisplayName,
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                if (instance is IWidgetActionPresentationProvider presentation)
                {
                    string? label = presentation.GetWidgetActionLabel(prop.Name);
                    if (!string.IsNullOrWhiteSpace(label)) btn.Content = label;

                    if (presentation.IsWidgetActionActive(prop.Name))
                    {
                        btn.Background = callbacks.TryFindResource("SuccessBackground") as Brush ?? btn.Background;
                        btn.BorderBrush = callbacks.TryFindResource("SuccessBorder") as Brush ?? btn.BorderBrush;
                        btn.BorderThickness = new Thickness(1);
                        btn.Foreground = callbacks.TryFindResource("M3Primary") as Brush ?? Brushes.White;
                    }
                }
                btn.Click += (_, _) =>
                {
                    if (instance is IWidgetActionInvoker invoker)
                        invoker.InvokeWidgetAction(prop.Name);
                };
                target.Add(btn);
                continue;
            }

            var propPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            propPanel.Children.Add(new TextBlock
            {
                Text = attr.DisplayName,
                FontSize = 11,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });

            object? currentVal = prop.GetValue(instance) ?? attr.DefaultValue;

            IReadOnlyList<WidgetPropertyOption> propertyOptions = attr.Options
                .Select(option => new WidgetPropertyOption(option, option))
                .ToArray();
            if (instance is IWidgetPropertyOptionsProvider optionsProvider)
            {
                var dynamicOptions = optionsProvider.GetPropertyOptions(prop.Name);
                if (dynamicOptions.Count > 0) propertyOptions = dynamicOptions;
            }

            if (attr.PropertyType == WidgetPropertyType.Choice && propertyOptions.Count > 0)
            {
                var combo = new ComboBox
                {
                    ItemsSource = propertyOptions,
                    DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
                    SelectedValuePath = nameof(WidgetPropertyOption.Value),
                    SelectedValue = currentVal?.ToString(),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                if (prop.Name == nameof(HotkeyButtonWidget.ActionType)) actionTypeCombo = combo;
                combo.SelectionChanged += (s, e) =>
                {
                    if (isUpdatingInspector) return;
                    string? selectedValue = combo.SelectedValue?.ToString();
                    if (!string.IsNullOrWhiteSpace(selectedValue))
                    {
                        prop.SetValue(instance, selectedValue);
                        instance.OnPropertyChanged(prop.Name, selectedValue);
                        widget.PropertyValues[prop.Name] = selectedValue;
                    }
                };
                callbacks.AttachDropdownWithinWindow(combo);
                propPanel.Children.Add(combo);
            }
            else if (attr.PropertyType == WidgetPropertyType.Font)
            {
                var combo = new ComboBox
                {
                    ItemsSource = propertyOptions.Count > 0
                        ? propertyOptions
                        : FontCatalog.GetAllFamilies().Select(family => new WidgetPropertyOption(family, family)).ToArray(),
                    DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
                    SelectedValuePath = nameof(WidgetPropertyOption.Value),
                    SelectedValue = currentVal?.ToString(),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                combo.SelectionChanged += (s, e) =>
                {
                    if (isUpdatingInspector) return;
                    string? selectedValue = combo.SelectedValue?.ToString();
                    if (!string.IsNullOrWhiteSpace(selectedValue))
                    {
                        prop.SetValue(instance, selectedValue);
                        instance.OnPropertyChanged(prop.Name, selectedValue);
                        widget.PropertyValues[prop.Name] = selectedValue;
                    }
                };
                callbacks.AttachDropdownWithinWindow(combo);
                propPanel.Children.Add(combo);
            }
            else if (attr.PropertyType == WidgetPropertyType.Icon)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                PropertyInfo? iconFileProp = typeof(HotkeyButtonWidget).GetProperty(nameof(HotkeyButtonWidget.IconFile));
                string seed = currentVal?.ToString() ?? "";
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
                    callbacks.ShowIconSelectorPopup(prop, hotkey, box);
                };
                box.TextChanged += (s, e) =>
                {
                    if (isUpdatingInspector) return;
                    callbacks.ApplyInspectorPropertyValue(iconFileProp, "");
                    callbacks.ApplyInspectorPropertyValue(prop, box.Text);
                };
                row.Children.Add(btnBrowse);
                row.Children.Add(box);
                propPanel.Children.Add(row);
            }
            else if (attr.PropertyType == WidgetPropertyType.Boolean)
            {
                var chk = new CheckBox
                {
                    Content = "Enabled / Active",
                    IsChecked = currentVal is bool b && b,
                    Foreground = Brushes.White
                };
                chk.Checked += (s, e) => { prop.SetValue(instance, true); instance.OnPropertyChanged(prop.Name, true); widget.PropertyValues[prop.Name] = true; };
                chk.Unchecked += (s, e) => { prop.SetValue(instance, false); instance.OnPropertyChanged(prop.Name, false); widget.PropertyValues[prop.Name] = false; };
                propPanel.Children.Add(chk);
            }
            else if (attr.PropertyType == WidgetPropertyType.Path)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                bool isHotkeyCommand = prop.DeclaringType == typeof(HotkeyButtonWidget) &&
                    prop.Name == nameof(HotkeyButtonWidget.ActionCommand);

                var txt = new TextBox { Text = currentVal?.ToString() ?? "" };
                txt.TextChanged += (s, e) =>
                {
                    if (isUpdatingInspector) return;
                    callbacks.ApplyInspectorPropertyValue(prop, txt.Text);
                };

                var btnFolder = new Button
                {
                    Content = "Folder\u2026",
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(4, 0, 0, 0)
                };
                DockPanel.SetDock(btnFolder, Dock.Right);
                btnFolder.Click += (s, e) =>
                {
                    var dlg = new OpenFolderDialog { Title = isHotkeyCommand ? "Select action folder" : "Select image folder" };
                    if (dlg.ShowDialog() == true)
                    {
                        txt.Text = dlg.FolderName;
                    }
                };

                var btnFile = new Button
                {
                    Content = "File\u2026",
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(4, 0, 0, 0)
                };
                DockPanel.SetDock(btnFile, Dock.Right);
                btnFile.Click += (s, e) =>
                {
                    var dlg = new OpenFileDialog
                    {
                        Title = isHotkeyCommand ? "Select action file or executable" : "Select image file",
                        Filter = isHotkeyCommand
                            ? "Programs and files (*.*)|*.*"
                            : "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        txt.Text = dlg.FileName;
                    }
                };

                row.Children.Add(btnFile);
                row.Children.Add(btnFolder);
                row.Children.Add(txt);
                propPanel.Children.Add(row);
                if (prop.Name == nameof(HotkeyButtonWidget.ActionCommand)) actionCommandPanel = propPanel;
            }
            else if (attr.PropertyType == WidgetPropertyType.SensorSelector)
            {
                var labels = LhmSensorStore.ReadSnapshot()
                    .Readings
                    .Select(r => r.Label)
                    .Distinct()
                    .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (labels.Count > 0)
                {
                    var combo = new ComboBox
                    {
                        ItemsSource = labels,
                        SelectedItem = currentVal?.ToString(),
                        Padding = new Thickness(8, 4, 8, 4)
                    };
                    combo.SelectionChanged += (s, e) =>
                    {
                        if (combo.SelectedItem != null)
                        {
                            prop.SetValue(instance, combo.SelectedItem.ToString());
                            instance.OnPropertyChanged(prop.Name, combo.SelectedItem.ToString());
                            widget.PropertyValues[prop.Name] = combo.SelectedItem.ToString();
                        }
                    };
                    callbacks.AttachDropdownWithinWindow(combo);
                    propPanel.Children.Add(combo);
                }
                else
                {
                    propPanel.Children.Add(new TextBlock
                    {
                        Text = "No sensors detected. Start the ModernWigiDash service, then reopen settings to pick a sensor.",
                        FontSize = 11,
                        Foreground = callbacks.TryFindResource("TextSecondary") as Brush ?? Brushes.White,
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }
            else
            {
                // Text or Number or Color
                var txt = new TextBox { Text = currentVal?.ToString() ?? "" };
                txt.TextChanged += (s, e) =>
                {
                    if (isUpdatingInspector) return;
                    string str = txt.Text;
                    if (prop.PropertyType == typeof(float) && float.TryParse(str, out float fVal))
                    {
                        prop.SetValue(instance, fVal);
                        instance.OnPropertyChanged(prop.Name, fVal);
                        widget.PropertyValues[prop.Name] = fVal;
                    }
                    else if (prop.PropertyType == typeof(int) && int.TryParse(str, out int iVal))
                    {
                        prop.SetValue(instance, iVal);
                        instance.OnPropertyChanged(prop.Name, iVal);
                        widget.PropertyValues[prop.Name] = iVal;
                    }
                    else if (prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(instance, str);
                        instance.OnPropertyChanged(prop.Name, str);
                        widget.PropertyValues[prop.Name] = str;
                    }
                };
                propPanel.Children.Add(txt);
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
}
