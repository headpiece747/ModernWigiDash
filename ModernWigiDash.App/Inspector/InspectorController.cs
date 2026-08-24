using System.Reflection;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// The right-side property inspector: panel refresh, transform write-backs,
/// property-value application (the single write-back seam the renderer uses),
/// and the dropdown-clamp hack. Owns the <c>isUpdatingInspector</c> guard:
/// enforced at the <see cref="ApplyPropertyValue"/> funnel (every editor
/// write-back) and in the transform/opacity handlers, so a programmatic set
/// during a panel rebuild never reaches the model. Value rules (parsing,
/// clamping, conversion, formatting) live in <see cref="InspectorValuePolicy"/>;
/// the icon picker dialog lives in <see cref="DialogHost"/>. The window keeps
/// selection and wiring only.
/// </summary>
internal sealed class InspectorController
{
    private readonly TransformFieldBindings _transform;
    private readonly CustomPropertyPanel _panel;
    private readonly Func<PlacedWidgetInstance?> _getSelectedWidget;
    private readonly InspectorValuePolicy _policy = new();
    private readonly DialogHost _dialogHost;
    private readonly Action? _onProfileChanged;
    private readonly Action<GeocodeCandidate>? _commitLocationPick;
    private bool _isUpdatingInspector = false;

    /// <param name="transform">The window's transform-face bindings (the six
    /// transform boxes, the opacity slider, the canvas-repaint request).</param>
    /// <param name="panel">The window's panel-face bindings (empty/active
    /// shells, name label, custom-properties host + focus bookkeeping).</param>
    /// <param name="getSelectedWidget">The window's selection probe — shared
    /// by both faces, so it stays a plain callback between them.</param>
    /// <param name="dialogHost">The window's single DialogHost instance. The
    /// inspector must not build its own — DialogHost is stateful (it owns the
    /// device-authorization window), and two instances for one owner would
    /// silently never show that window.</param>
    /// <param name="onProfileChanged">Invoked after a write-back lands in the
    /// profile model (transform/opacity/property values) so the owner can arm
    /// profile persistence. This callback IS the dirty mark on the
    /// inspector-driven path — exactly one invocation per landed write-back,
    /// the window's forwarding handlers add none.</param>
    /// <param name="commitLocationPick">Invoked when the user picks a location
    /// search result; the owner resolves the selected widget's
    /// <see cref="IWidgetLocationSearch"/> and commits the candidate.</param>
    public InspectorController(
        TransformFieldBindings transform,
        CustomPropertyPanel panel,
        Func<PlacedWidgetInstance?> getSelectedWidget,
        DialogHost dialogHost,
        Action? onProfileChanged = null,
        Action<GeocodeCandidate>? commitLocationPick = null)
    {
        _transform = transform;
        _panel = panel;
        _getSelectedWidget = getSelectedWidget;
        _dialogHost = dialogHost;
        _onProfileChanged = onProfileChanged;
        _commitLocationPick = commitLocationPick;
        // The policy's default warning sink is Debug.WriteLine; the controller
        // routes warnings into the shared file log so conversion failures
        // surface in the field, not only in a debugger.
        _policy.LogWarning = msg => FileLog.Write("[INSPECTOR] " + msg);
    }

    /// <summary>Rebuilds the panel for the currently selected widget (or the empty state).</summary>
    public void Refresh()
    {
        var selected = _getSelectedWidget();
        if (selected is null)
        {
            _panel.ShowEmptyState();
            return;
        }

        _isUpdatingInspector = true;
        try
        {
            _panel.ShowWidget(selected.DisplayName);

            _transform.PosX.Text = _policy.FormatTransformValue(selected.X);
            _transform.PosY.Text = _policy.FormatTransformValue(selected.Y);
            _transform.WidthText.Text = _policy.FormatTransformValue(selected.Width);
            _transform.HeightText.Text = _policy.FormatTransformValue(selected.Height);
            _transform.ZIndexText.Text = _policy.FormatValue(selected.ZIndex);
            _transform.RotationText.Text = _policy.FormatTransformValue(selected.Rotation);
            _transform.OpacitySlider.Value = selected.Opacity;
            _transform.OpacityValueText.Text = _policy.FormatOpacityPercent(selected.Opacity);

            // Remember which custom-property row owned focus (and where the
            // caret was) before the rebuild: the panel is cleared and
            // re-rendered below, which would otherwise eject the user from the
            // field they are typing in (the weather widget's inspector refresh
            // fires while Location is being edited).
            var (focusedRow, focusedCaret) = _panel.CaptureFocusState();

            // Build dynamic custom property editors for the widget
            _panel.CustomProperties.Children.Clear();
            if (selected.ActiveInstance is not null)
            {
                // The sensor picker's live labels come from the store — read
                // once per refresh at the composition site, then passed into
                // the pure mapping module (never per Describe call).
                var sensorOptions = InspectorModelBuilder.SensorOptions(LhmSensorStore.ReadSnapshot());

                InspectorPanelRenderer.Render(
                    selected,
                    InspectorModelBuilder.Describe(selected, sensorOptions),
                    _panel.CustomProperties.Children,
                    new InspectorCallbacks
                    {
                        TryFindResource = name => _panel.TryFindResource(name),
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
                        },
                        CommitLocationPick = _commitLocationPick
                    });

                // Restore focus to the same property's editor so typing and
                // caret placement survive the refresh. The rebuilt row sits at
                // the same index as the old one (one row per property).
                _panel.RestoreFocusToRow(focusedRow, focusedCaret);
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
        var selected = _getSelectedWidget();
        if (selected is null) return;
        _isUpdatingInspector = true;
        try
        {
            _transform.PosX.Text = _policy.FormatTransformValue(selected.X);
            _transform.PosY.Text = _policy.FormatTransformValue(selected.Y);
            _transform.WidthText.Text = _policy.FormatTransformValue(selected.Width);
            _transform.HeightText.Text = _policy.FormatTransformValue(selected.Height);
        }
        finally
        {
            _isUpdatingInspector = false;
        }
    }

    /// <summary>
    /// Single write-back seam: converts a raw value (TextBox strings arrive as
    /// text) to the property's CLR type and writes it into the widget instance
    /// and the profile. The renderer never writes the model directly. The
    /// re-entrancy guard is enforced HERE, at the funnel: every editor
    /// write-back routes through this seam, so a programmatic set during a
    /// panel rebuild is suppressed for every editor type — an unguarded
    /// builder is unrepresentable.
    /// </summary>
    public void ApplyPropertyValue(PropertyInfo? prop, object value)
    {
        if (_isUpdatingInspector) return;

        var selected = _getSelectedWidget();
        if (selected?.ActiveInstance is null || prop is null) return;

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
        _onProfileChanged?.Invoke();
    }

    /// <summary>XAML <c>Transform_Changed</c> handler: position/size/rotation write-backs.</summary>
    public void TransformChanged(object sender, TextChangedEventArgs e)
    {
        var selected = _getSelectedWidget();
        if (_isUpdatingInspector || selected is null) return;
        if (sender is not TextBox box) return;

        // Parse only the box that fired (the old code re-parsed and re-wrote
        // all six on any change), and arm the save mark only when a value
        // actually landed — unparseable input must not dirty the profile or
        // repaint the canvas.
        if (!ApplyTransformBox(box, selected)) return;

        _onProfileChanged?.Invoke();
        _transform.RequestCanvasRender();
    }

    /// <summary>
    /// Parses and applies the single transform box that changed; true when a
    /// value landed (the caller arms the save mark and repaints).
    /// </summary>
    private bool ApplyTransformBox(TextBox box, PlacedWidgetInstance selected)
    {
        if (ReferenceEquals(box, _transform.PosX) && _policy.TryParsePosition(box.Text, out float x)) { selected.X = x; return true; }
        if (ReferenceEquals(box, _transform.PosY) && _policy.TryParsePosition(box.Text, out float y)) { selected.Y = y; return true; }
        if (ReferenceEquals(box, _transform.WidthText) && _policy.TryParseSize(box.Text, out float w)) { selected.Width = w; return true; }
        if (ReferenceEquals(box, _transform.HeightText) && _policy.TryParseSize(box.Text, out float h)) { selected.Height = h; return true; }
        if (ReferenceEquals(box, _transform.ZIndexText) && _policy.TryParseZIndex(box.Text, out int z)) { selected.ZIndex = z; return true; }
        if (ReferenceEquals(box, _transform.RotationText) && _policy.TryParseRotation(box.Text, out float r)) { selected.Rotation = r; return true; }
        return false;
    }

    /// <summary>XAML <c>SliderOpacity_ValueChanged</c> handler.</summary>
    public void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var selected = _getSelectedWidget();
        if (_isUpdatingInspector || selected is null) return;

        selected.Opacity = _policy.ClampOpacity((float)_transform.OpacitySlider.Value);
        _transform.OpacityValueText.Text = _policy.FormatOpacityPercent(selected.Opacity);
        _onProfileChanged?.Invoke();
        _transform.RequestCanvasRender();
    }

    /// <summary>
    /// Icon picker entry for an <see cref="EditorKind.IconPicker"/> property.
    /// Reads the current icon/file values from the provider (the provider IS
    /// the widget instance — no concrete widget type needed), shows the picker
    /// via <see cref="DialogHost.ShowIconPicker"/>, and writes the chosen value
    /// back through <see cref="ApplyPropertyValue"/>; the named-vs-custom
    /// verdict, the read precedence, and the companion mutual exclusion are
    /// the <see cref="IconValuePolicy"/> decisions.
    /// </summary>
    public void ShowIconSelectorPopup(PropertyInfo iconProp, IWidgetEditorProvider provider, TextBox box)
    {
        PropertyInfo? iconFileProp = provider.GetIconFileCompanion(iconProp);
        string? currentIconFile = iconFileProp?.GetValue(provider) as string;
        string? currentIcon = iconProp.GetValue(provider) as string;

        string current = IconValuePolicy.ResolveCurrent(currentIcon, currentIconFile);
        string? chosen = _dialogHost.ShowIconPicker("Select Icon", current);
        if (string.IsNullOrWhiteSpace(chosen)) return;

        (string named, string iconFile) = IconValuePolicy.SplitWriteback(chosen);
        ApplyPropertyValue(iconFileProp, iconFile);
        ApplyPropertyValue(iconProp, named);

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
            if (Window.GetWindow(combo) is null) return;
            if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;

            PopupClamp.AttachPopupWithinWindow(popup, combo);
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
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } inner) return inner;
        }
        return null;
    }
}
