using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Host wiring for the inspector controller: the concrete WPF panel controls
/// the window owns, plus the few callbacks the controller needs to reach back
/// into the window (resource lookup, selection, canvas repaint).
/// </summary>
public sealed class InspectorControllerHost(
    Window owner,
    UIElement emptyPanel,
    UIElement activePanel,
    TextBlock nameText,
    TextBox posX,
    TextBox posY,
    TextBox widthText,
    TextBox heightText,
    TextBox zIndexText,
    TextBox rotationText,
    Slider opacitySlider,
    TextBlock opacityValueText,
    StackPanel customProperties,
    Func<string, object?> tryFindResource,
    Func<PlacedWidgetInstance?> getSelectedWidget,
    Action requestCanvasRender)
{
    public Window Owner { get; } = owner;
    public UIElement EmptyPanel { get; } = emptyPanel;
    public UIElement ActivePanel { get; } = activePanel;
    public TextBlock NameText { get; } = nameText;
    public TextBox PosX { get; } = posX;
    public TextBox PosY { get; } = posY;
    public TextBox WidthText { get; } = widthText;
    public TextBox HeightText { get; } = heightText;
    public TextBox ZIndexText { get; } = zIndexText;
    public TextBox RotationText { get; } = rotationText;
    public Slider OpacitySlider { get; } = opacitySlider;
    public TextBlock OpacityValueText { get; } = opacityValueText;
    public StackPanel CustomProperties { get; } = customProperties;
    public Func<string, object?> TryFindResource { get; } = tryFindResource;
    public Func<PlacedWidgetInstance?> GetSelectedWidget { get; } = getSelectedWidget;
    public Action RequestCanvasRender { get; } = requestCanvasRender;
}

/// <summary>
/// The right-side property inspector: panel refresh, transform write-backs,
/// property-value application (the single write-back seam the renderer uses),
/// the icon picker dialog, and the dropdown-clamp hack. Owns the
/// <c>isUpdatingInspector</c> guard that suppresses change events while the
/// panel is rebuilt. The window keeps selection and wiring only.
/// </summary>
public sealed class InspectorController
{
    private readonly InspectorControllerHost _host;
    private bool _isUpdatingInspector = false;

    public InspectorController(InspectorControllerHost host)
    {
        _host = host;
    }

    /// <summary>Rebuilds the panel for the currently selected widget (or the empty state).</summary>
    public void Refresh()
    {
        var selected = _host.GetSelectedWidget();
        if (selected == null)
        {
            _host.EmptyPanel.Visibility = Visibility.Visible;
            _host.ActivePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _isUpdatingInspector = true;
        try
        {
            _host.EmptyPanel.Visibility = Visibility.Collapsed;
            _host.ActivePanel.Visibility = Visibility.Visible;

            _host.NameText.Text = selected.DisplayName;

            _host.PosX.Text = $"{selected.X:F0}";
            _host.PosY.Text = $"{selected.Y:F0}";
            _host.WidthText.Text = $"{selected.Width:F0}";
            _host.HeightText.Text = $"{selected.Height:F0}";
            _host.ZIndexText.Text = $"{selected.ZIndex}";
            _host.RotationText.Text = $"{selected.Rotation:F0}";
            _host.OpacitySlider.Value = selected.Opacity;
            _host.OpacityValueText.Text = $"{(int)(selected.Opacity * 100)}%";

            // Build dynamic custom property editors for the widget
            _host.CustomProperties.Children.Clear();
            if (selected.ActiveInstance != null)
            {
                InspectorPanelRenderer.Render(
                    selected,
                    InspectorModelBuilder.Describe(selected),
                    _host.CustomProperties.Children,
                    () => _isUpdatingInspector,
                    new InspectorCallbacks
                    {
                        TryFindResource = name => _host.TryFindResource(name),
                        ApplyInspectorPropertyValue = ApplyPropertyValue,
                        ShowIconSelectorPopup = ShowIconSelectorPopup,
                        AttachDropdownWithinWindow = AttachDropdownWithinWindow,
                        BrowseFile = (title, filter) =>
                        {
                            var dlg = new OpenFileDialog { Title = title, Filter = filter };
                            return dlg.ShowDialog() == true ? dlg.FileName : null;
                        },
                        BrowseFolder = title =>
                        {
                            var dlg = new OpenFolderDialog { Title = title };
                            return dlg.ShowDialog() == true ? dlg.FolderName : null;
                        }
                    });
            }
        }
        finally
        {
            _isUpdatingInspector = false;
        }
    }

    /// <summary>Refreshes only the transform text boxes (used during drag/resize).</summary>
    public void RefreshTransforms()
    {
        var selected = _host.GetSelectedWidget();
        if (selected == null) return;
        _isUpdatingInspector = true;
        try
        {
            _host.PosX.Text = $"{selected.X:F0}";
            _host.PosY.Text = $"{selected.Y:F0}";
            _host.WidthText.Text = $"{selected.Width:F0}";
            _host.HeightText.Text = $"{selected.Height:F0}";
        }
        finally
        {
            _isUpdatingInspector = false;
        }
    }

    /// <summary>
    /// Single write-back seam: converts a raw value (TextBox strings arrive as
    /// text) to the property's CLR type and writes it into the widget instance
    /// and the profile. The renderer never writes the model directly.
    /// </summary>
    public void ApplyPropertyValue(PropertyInfo? prop, object value)
    {
        var selected = _host.GetSelectedWidget();
        if (selected?.ActiveInstance == null || prop == null) return;

        object? converted = value;
        // TextBox input arrives as string; convert to the property's CLR type
        // so a Number/Color/etc. property is never silently dropped by a
        // SetValue type mismatch.
        if (value is string str && prop.PropertyType != typeof(string))
        {
            try
            {
                converted = TypeDescriptor.GetConverter(prop.PropertyType).ConvertFromInvariantString(str);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Inspector value '{str}' not convertible to {prop.PropertyType.Name} for {prop.Name}: {ex.Message}");
                return;
            }
        }

        prop.SetValue(selected.ActiveInstance, converted);
        selected.ActiveInstance.OnPropertyChanged(prop.Name, converted);
        selected.PropertyValues[prop.Name] = converted;
    }

    /// <summary>XAML <c>Transform_Changed</c> handler: position/size/rotation/opacity write-backs.</summary>
    public void TransformChanged(object sender, TextChangedEventArgs e)
    {
        var selected = _host.GetSelectedWidget();
        if (_isUpdatingInspector || selected == null) return;

        if (float.TryParse(_host.PosX.Text, out float x)) selected.X = x;
        if (float.TryParse(_host.PosY.Text, out float y)) selected.Y = y;
        if (float.TryParse(_host.WidthText.Text, out float w) && w > 20) selected.Width = w;
        if (float.TryParse(_host.HeightText.Text, out float h) && h > 20) selected.Height = h;
        if (int.TryParse(_host.ZIndexText.Text, out int z)) selected.ZIndex = z;
        if (float.TryParse(_host.RotationText.Text, out float r)) selected.Rotation = r % 360;

        _host.RequestCanvasRender();
    }

    /// <summary>XAML <c>SliderOpacity_ValueChanged</c> handler.</summary>
    public void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var selected = _host.GetSelectedWidget();
        if (selected != null && _host.OpacityValueText != null)
        {
            selected.Opacity = (float)_host.OpacitySlider.Value;
            _host.OpacityValueText.Text = $"{(int)(selected.Opacity * 100)}%";
            _host.RequestCanvasRender();
        }
    }

    /// <summary>
    /// Icon picker dialog for an <see cref="EditorKind.IconPicker"/> property.
    /// Reads/writes the widget's properties via reflection on the provider
    /// (the provider IS the widget instance) — no concrete widget type needed.
    /// </summary>
    public void ShowIconSelectorPopup(PropertyInfo iconProp, IWidgetEditorProvider provider, TextBox box)
    {
        PropertyInfo? iconFileProp = provider.GetIconFileCompanion(iconProp);
        string? currentIconFile = iconFileProp?.GetValue(provider) as string;
        string? currentIcon = iconProp.GetValue(provider) as string;

        var dialog = new Window
        {
            Title = "Select Icon",
            Width = 520,
            Height = 620,
            Owner = _host.Owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("BgPanel", Brush("PanelBackground", Brushes.Black)),
            Foreground = Brushes.White
        };
        dialog.SourceInitialized += (_, _) => WindowChrome.ApplyDarkTitleBar(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var search = new TextBox { ToolTip = "Search icons by name", Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(search, 0);
        root.Children.Add(search);

        var browseSvg = new Button
        {
            Content = "Browse SVG\u2026",
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var chip = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextSecondary", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var browseRow = new StackPanel { Orientation = Orientation.Horizontal };
        browseRow.Children.Add(browseSvg);
        browseRow.Children.Add(chip);
        Grid.SetRow(browseRow, 1);
        root.Children.Add(browseRow);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        var grid = new WrapPanel { ItemWidth = 40, ItemHeight = 40 };
        scroll.Content = grid;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var selectedName = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var select = new Button
        {
            Content = "Select",
            Padding = new Thickness(14, 5, 14, 5),
            Style = _host.TryFindResource("AccentButton") as Style
        };
        Grid.SetColumn(selectedName, 0);
        Grid.SetColumn(select, 1);
        footer.Children.Add(selectedName);
        footer.Children.Add(select);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        string chosen = "";
        void UpdateSelected(string name)
        {
            chosen = name;
            selectedName.Text = name;
        }

        void RenderGrid()
        {
            grid.Children.Clear();
            string filter = search.Text?.Trim() ?? "";
            var names = string.IsNullOrEmpty(filter)
                ? GriddyIcons.Names
                : GriddyIcons.Names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var name in names)
            {
                var cell = new Button
                {
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    Tag = name,
                    ToolTip = name,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Transparent
                };
                if (GriddyIcons.TryGetPathData(name, out string? pathData))
                {
                    try
                    {
                        cell.Content = new System.Windows.Shapes.Path
                        {
                            Width = 22,
                            Height = 22,
                            Stretch = Stretch.Uniform,
                            Fill = Brushes.White,
                            Data = Geometry.Parse(pathData)
                        };
                    }
                    catch
                    {
                        cell.Content = null;
                    }
                }
                if (name.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                    cell.BorderBrush = Brush("AccentRed", Brushes.Red);
                cell.Click += (_, _) =>
                {
                    UpdateSelected(name);
                    foreach (var child in grid.Children.OfType<Button>())
                        child.BorderBrush = Brushes.Transparent;
                    cell.BorderBrush = Brush("AccentRed", Brushes.Red);
                };
                grid.Children.Add(cell);
            }
        }

        search.TextChanged += (_, _) => RenderGrid();

        browseSvg.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "Select an SVG icon", Filter = "SVG files (*.svg)|*.svg" };
            if (dlg.ShowDialog() != true) return;
            if (!SvgIconLoader.TryGetPath(dlg.FileName, out _))
            {
                MessageBox.Show(dialog, "Only single-path SVG icons are supported.", "Unsupported SVG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string relative = SvgIconLoader.CopyToIcons(dlg.FileName);
            ApplyPropertyValue(iconFileProp, relative);
            ApplyPropertyValue(iconProp, "");
            chip.Text = $"Custom: {relative}";
            _isUpdatingInspector = true;
            try
            {
                box.Text = relative;
            }
            finally
            {
                _isUpdatingInspector = false;
            }
            UpdateSelected(relative);
        };

        select.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (GriddyIcons.Contains(chosen))
            {
                ApplyPropertyValue(iconFileProp, "");
                ApplyPropertyValue(iconProp, chosen);
                _isUpdatingInspector = true;
                try
                {
                    box.Text = chosen;
                }
                finally
                {
                    _isUpdatingInspector = false;
                }
            }
            dialog.DialogResult = true;
        };

        if (!string.IsNullOrWhiteSpace(currentIconFile))
        {
            chip.Text = $"Custom: {currentIconFile}";
            chosen = currentIconFile;
            selectedName.Text = currentIconFile;
        }
        else
        {
            chosen = currentIcon ?? "";
            selectedName.Text = currentIcon ?? "";
        }
        RenderGrid();
        dialog.Content = root;
        dialog.ShowDialog();
    }

    /// <summary>
    /// Keeps a ComboBox dropdown inside the window's client area. WPF positions the
    /// popup against the screen, so a dropdown near the window's bottom edge extends
    /// below the window where its options can't be clicked. This flips the dropdown
    /// upward (or clamps it) and caps its height so every option stays inside the window.
    /// </summary>
    private static void AttachDropdownWithinWindow(ComboBox combo)
    {
        combo.Loaded += (_, _) =>
        {
            combo.ApplyTemplate();
            if (Window.GetWindow(combo) is not Window window) return;
            if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;
            if (window.Content is not Visual content) return;

            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                double clientW = (content as FrameworkElement)?.ActualWidth ?? window.ActualWidth;
                double clientH = (content as FrameworkElement)?.ActualHeight ?? window.ActualHeight;
                var tl = combo.TransformToAncestor(content).Transform(new Point(0, 0));

                List<CustomPopupPlacement> placements = [];
                if (clientH - (tl.Y + targetSize.Height) >= popupSize.Height)
                {
                    placements.Add(new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal));
                }
                if (tl.Y >= popupSize.Height)
                {
                    placements.Add(new CustomPopupPlacement(new Point(0, -popupSize.Height), PopupPrimaryAxis.Horizontal));
                }

                double popupLeft = Math.Clamp(tl.X, 0, Math.Max(0, clientW - popupSize.Width));
                double popupTop = Math.Clamp(tl.Y + targetSize.Height, 0, Math.Max(0, clientH - popupSize.Height));
                placements.Add(new CustomPopupPlacement(new Point(popupLeft - tl.X, popupTop - tl.Y), PopupPrimaryAxis.Horizontal));
                return placements.ToArray();
            };
        };

        combo.DropDownOpened += (_, _) =>
        {
            if (Window.GetWindow(combo) is not Window window) return;
            if (window.Content is not FrameworkElement content) return;
            if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;

            var tl = combo.TransformToAncestor(content).Transform(new Point(0, 0));
            double below = content.ActualHeight - (tl.Y + combo.ActualHeight);
            double above = tl.Y;
            double available = Math.Max(120, Math.Max(below, above) - 10);

            if (popup.Child is FrameworkElement popupContent &&
                FindVisualChild<ScrollViewer>(popupContent) is ScrollViewer scroll)
            {
                scroll.MaxHeight = available;
            }
        };
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is T inner) return inner;
        }
        return null;
    }

    private Brush Brush(string name, Brush fallback)
        => _host.TryFindResource(name) as Brush ?? fallback;
}
