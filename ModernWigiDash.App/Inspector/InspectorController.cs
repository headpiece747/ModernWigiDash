using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
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
/// and the dropdown-clamp hack. Owns the <c>isUpdatingInspector</c> guard that
/// suppresses change events while the panel is rebuilt. Value rules (parsing,
/// clamping, conversion, formatting) live in <see cref="InspectorValuePolicy"/>;
/// the icon picker dialog lives in <see cref="DialogHost"/>. The window keeps
/// selection and wiring only.
/// </summary>
public sealed class InspectorController
{
    private readonly InspectorControllerHost _host;
    private readonly InspectorValuePolicy _policy = new();
    private readonly DialogHost _dialogHost;
    private bool _isUpdatingInspector = false;

    /// <param name="host">The window's wiring holder.</param>
    /// <param name="dialogHost">The window's single DialogHost instance. The
    /// inspector must not build its own — DialogHost is stateful (it owns the
    /// device-authorization window), and two instances for one owner would
    /// silently never show that window.</param>
    public InspectorController(InspectorControllerHost host, DialogHost dialogHost)
    {
        _host = host;
        _dialogHost = dialogHost;
        // The policy's default warning sink is Debug.WriteLine; the controller
        // routes warnings into the shared file log so conversion failures
        // surface in the field, not only in a debugger.
        _policy.LogWarning = msg => FileLog.Write("[INSPECTOR] " + msg);
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

            _host.PosX.Text = _policy.FormatTransformValue(selected.X);
            _host.PosY.Text = _policy.FormatTransformValue(selected.Y);
            _host.WidthText.Text = _policy.FormatTransformValue(selected.Width);
            _host.HeightText.Text = _policy.FormatTransformValue(selected.Height);
            _host.ZIndexText.Text = _policy.FormatValue(selected.ZIndex);
            _host.RotationText.Text = _policy.FormatTransformValue(selected.Rotation);
            _host.OpacitySlider.Value = selected.Opacity;
            _host.OpacityValueText.Text = _policy.FormatOpacityPercent(selected.Opacity);

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
            _host.PosX.Text = _policy.FormatTransformValue(selected.X);
            _host.PosY.Text = _policy.FormatTransformValue(selected.Y);
            _host.WidthText.Text = _policy.FormatTransformValue(selected.Width);
            _host.HeightText.Text = _policy.FormatTransformValue(selected.Height);
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
        if (value is string str)
        {
            if (!_policy.TryConvertStringToType(prop, str, out object? convertedValue)) return;
            converted = convertedValue;
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

        if (_policy.TryParsePosition(_host.PosX.Text, out float x)) selected.X = x;
        if (_policy.TryParsePosition(_host.PosY.Text, out float y)) selected.Y = y;
        if (_policy.TryParseSize(_host.WidthText.Text, out float w)) selected.Width = w;
        if (_policy.TryParseSize(_host.HeightText.Text, out float h)) selected.Height = h;
        if (_policy.TryParseZIndex(_host.ZIndexText.Text, out int z)) selected.ZIndex = z;
        if (_policy.TryParseRotation(_host.RotationText.Text, out float r)) selected.Rotation = r;

        _host.RequestCanvasRender();
    }

    /// <summary>XAML <c>SliderOpacity_ValueChanged</c> handler.</summary>
    public void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var selected = _host.GetSelectedWidget();
        if (_isUpdatingInspector || selected == null) return;

        selected.Opacity = _policy.ClampOpacity((float)_host.OpacitySlider.Value);
        _host.OpacityValueText.Text = _policy.FormatOpacityPercent(selected.Opacity);
        _host.RequestCanvasRender();
    }

    /// <summary>
    /// Icon picker entry for an <see cref="EditorKind.IconPicker"/> property.
    /// Reads the current icon/file values from the provider (the provider IS
    /// the widget instance — no concrete widget type needed), shows the picker
    /// via <see cref="DialogHost.ShowIconPicker"/>, and writes the chosen value
    /// back through <see cref="ApplyPropertyValue"/>, keeping the companion
    /// file property and the named icon mutually exclusive.
    /// </summary>
    public void ShowIconSelectorPopup(PropertyInfo iconProp, IWidgetEditorProvider provider, TextBox box)
    {
        PropertyInfo? iconFileProp = provider.GetIconFileCompanion(iconProp);
        string? currentIconFile = iconFileProp?.GetValue(provider) as string;
        string? currentIcon = iconProp.GetValue(provider) as string;

        string current = !string.IsNullOrWhiteSpace(currentIconFile) ? currentIconFile : currentIcon ?? "";
        string? chosen = _dialogHost.ShowIconPicker("Select Icon", current);
        if (string.IsNullOrWhiteSpace(chosen)) return;

        if (GriddyIcons.Contains(chosen))
        {
            ApplyPropertyValue(iconFileProp, "");
            ApplyPropertyValue(iconProp, chosen);
        }
        else
        {
            ApplyPropertyValue(iconFileProp, chosen);
            ApplyPropertyValue(iconProp, "");
        }

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
}
