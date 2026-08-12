using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Controls;

/// <summary>
/// One reusable color-editing row: a swatch button and (optionally) a hex
/// text box, hosting a <see cref="ColorPickerPopup"/> in a clamped Popup.
/// Commits surface through <see cref="Applied"/> — the popup's Apply or a
/// valid live hex-box change — so consumers (inspector, theme dialog, page
/// background) all wire one event. Programmatic <see cref="Hex"/> sets never
/// raise events (panel rebuilds must not commit).
/// </summary>
public sealed class ColorPickerEditor : UserControl
{
    private readonly Border _swatch;
    private string _hex = "";
    private bool _suppress;

    internal TextBox HexBox { get; }
    internal Button SwatchButton { get; }
    internal Popup Popup { get; }
    internal ColorPickerPopup PopupContent { get; }

    public ColorPickerEditor()
    {
        var row = new DockPanel();

        SwatchButton = new Button
        {
            Width = 34, Height = 24, Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(0), BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(1), ToolTip = "Pick a color"
        };
        _swatch = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(3) };
        SwatchButton.Content = _swatch;

        HexBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };

        row.Children.Add(SwatchButton);
        row.Children.Add(HexBox);

        PopupContent = new ColorPickerPopup(new RgbaColor(255, 0, 0, 0));
        Popup = new Popup
        {
            PlacementTarget = SwatchButton,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = PopupContent
            }
        };

        SwatchButton.Click += (_, _) =>
        {
            PopupContent.SetFromHex(_hex); // internal reuse: sync popup state
            PopupClamp.AttachPopupWithinWindow(Popup, SwatchButton);
            Popup.IsOpen = true;
        };

        HexBox.TextChanged += (_, _) =>
        {
            if (_suppress) return;
            _hex = HexBox.Text.Trim();
            IsValidHex = ThemeSettings.ParseColor(_hex) is not null;
            HexBox.BorderBrush = IsValidHex ? null : Brushes.Red;
            SyncSwatch();
            Changed?.Invoke();
            if (IsValidHex) Applied?.Invoke(_hex);
        };

        PopupContent.Applied += hex =>
        {
            Popup.IsOpen = false;
            SetHexSilently(hex);
            IsValidHex = true;
            Applied?.Invoke(hex);
        };
        PopupContent.Cancelled += () => Popup.IsOpen = false;

        Content = row;
    }

    /// <summary>Current hex value (#RRGGBB or #AARRGGBB). Setting it updates the
    /// swatch and hex box without raising <see cref="Applied"/>.</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            _hex = value.Trim();
            IsValidHex = ThemeSettings.ParseColor(_hex) is not null;
            SetHexSilently(_hex);
            SyncSwatch();
            PopupContent.SetFromHex(_hex); // keep the popup preview in sync with the row value
        }
    }

    /// <summary>False hides the hex box (swatch-only mode for the page bar).</summary>
    public bool ShowHexBox
    {
        get => HexBox.Visibility == Visibility.Visible;
        set => HexBox.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsValidHex { get; private set; }

    /// <summary>Raised on commit: popup Apply, or a valid live hex-box change.</summary>
    public event Action<string>? Applied;

    /// <summary>Raised on every hex-box text change (validation hook).</summary>
    public event Action? Changed;

    private void SetHexSilently(string hex)
    {
        _suppress = true;
        try { HexBox.Text = hex; }
        finally { _suppress = false; }
    }

    private void SyncSwatch()
    {
        var color = ThemeSettings.ParseColor(_hex);
        var brush = color is { } c
            ? new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B))
            : new SolidColorBrush(Color.FromRgb(18, 20, 29));
        _swatch.Background = brush;
        SwatchButton.Background = brush;
    }
}
