using System.Windows.Shapes;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Controls;

/// <summary>
/// The color picker popup editor: curated presets, an SV square, hue and alpha
/// strips, a hex field, and Apply/Cancel. Pure preview state inside — nothing
/// is committed until Apply (or the hosting row's live hex box) raises Applied.
/// </summary>
internal sealed class ColorPickerPopup : UserControl
{
    private HsvColor _hsv;
    private byte _alpha;
    private readonly Canvas _svCanvas;
    private readonly Rectangle _svHueLayer;
    private readonly Border _svThumb;
    private readonly Canvas _hueCanvas;
    private readonly Border _hueThumb;
    private readonly Slider _alphaSlider;
    private readonly TextBox _hexBox;
    private bool _suppress;

    /// <summary>Apply / Cancel buttons exposed for tests.</summary>
    internal Button ApplyButton { get; }
    internal Button CancelButton { get; }
    internal WrapPanel PresetPanel { get; }
    internal Canvas SvCanvas => _svCanvas;

    public ColorPickerPopup(RgbaColor initial)
    {
        _hsv = ColorConversions.RgbToHsv(initial);
        _alpha = initial.A;

        var root = new StackPanel { Width = 252 };

        // Presets
        PresetPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var swatch in PresetPalette.Swatches)
        {
            var btn = new Button
            {
                Content = "",
                Width = 22,
                Height = 18,
                Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(HexToMediaColor(swatch.Hex)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                ToolTip = swatch.Name
            };
            btn.Click += (_, _) => SetFromHex(swatch.Hex);
            PresetPanel.Children.Add(btn);
        }
        root.Children.Add(PresetPanel);

        // SV square: hue base + white (horizontal) + black (vertical) overlays.
        // All children are IsHitTestVisible=false (the thumb must not swallow
        // drags), so the canvas needs an explicit transparent Background — a
        // panel with a null Background is invisible to WPF hit testing and the
        // drag handlers would never fire.
        _svCanvas = new Canvas { Width = 252, Height = 130, ClipToBounds = true, Background = Brushes.Transparent };
        _svHueLayer = new Rectangle { Width = 252, Height = 130, IsHitTestVisible = false };
        var svWhiteLayer = new Rectangle
        {
            Width = 252,
            Height = 130,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, 0)
        };
        var svBlackLayer = new Rectangle
        {
            Width = 252,
            Height = 130,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90)
        };
        _svThumb = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_svThumb, -7); Canvas.SetTop(_svThumb, -7);
        _svCanvas.Children.Add(_svHueLayer);
        _svCanvas.Children.Add(svWhiteLayer);
        _svCanvas.Children.Add(svBlackLayer);
        _svCanvas.Children.Add(_svThumb);
        root.Children.Add(_svCanvas);

        // Hue strip (Background for the same hit-test reason as the SV square)
        _hueCanvas = new Canvas { Width = 252, Height = 16, Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true, Background = Brushes.Transparent };
        var hueStrip = new Rectangle
        {
            Width = 252,
            Height = 16,
            Fill = new LinearGradientBrush(
            [
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Yellow, 1d / 6),
                new GradientStop(Colors.Lime, 2d / 6),
                new GradientStop(Colors.Cyan, 3d / 6),
                new GradientStop(Colors.Blue, 4d / 6),
                new GradientStop(Colors.Magenta, 5d / 6),
                new GradientStop(Colors.Red, 1)
            ], 0)
        };
        _hueThumb = new Border
        {
            Width = 10,
            Height = 16,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_hueThumb, -5);
        _hueCanvas.Children.Add(hueStrip);
        _hueCanvas.Children.Add(_hueThumb);
        root.Children.Add(_hueCanvas);

        // Alpha slider
        var alphaLabel = new TextBlock { Text = "Opacity", FontSize = 10, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 2) };
        _alphaSlider = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true };
        _alphaSlider.ValueChanged += (_, e) => { _alpha = (byte)e.NewValue; UpdatePreview(); };
        root.Children.Add(alphaLabel);
        root.Children.Add(_alphaSlider);

        // Hex field + buttons
        var hexRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _hexBox = new TextBox { Text = ColorConversions.FormatHex(initial) };
        _hexBox.TextChanged += (_, _) => { if (!_suppress) SetFromHex(_hexBox.Text); };
        CancelButton = new Button { Content = "Cancel", Padding = new Thickness(8, 2, 8, 2) };
        ApplyButton = new Button { Content = "Apply", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(CancelButton, Dock.Right);
        DockPanel.SetDock(ApplyButton, Dock.Right);
        hexRow.Children.Add(CancelButton);
        hexRow.Children.Add(ApplyButton);
        hexRow.Children.Add(_hexBox);
        root.Children.Add(hexRow);

        ApplyButton.Click += (_, _) => Applied?.Invoke(ColorConversions.FormatHex(CurrentColor));
        CancelButton.Click += (_, _) => Cancelled?.Invoke();

        // Interaction
        _svCanvas.MouseLeftButtonDown += (_, e) => { _svCanvas.CaptureMouse(); UpdateSvFromPoint(e.GetPosition(_svCanvas)); };
        _svCanvas.MouseMove += (_, e) => { if (_svCanvas.IsMouseCaptured) UpdateSvFromPoint(e.GetPosition(_svCanvas)); };
        _svCanvas.MouseLeftButtonUp += (_, _) => _svCanvas.ReleaseMouseCapture();
        _hueCanvas.MouseLeftButtonDown += (_, e) => { _hueCanvas.CaptureMouse(); UpdateHueFromPoint(e.GetPosition(_hueCanvas)); };
        _hueCanvas.MouseMove += (_, e) => { if (_hueCanvas.IsMouseCaptured) UpdateHueFromPoint(e.GetPosition(_hueCanvas)); };
        _hueCanvas.MouseLeftButtonUp += (_, _) => _hueCanvas.ReleaseMouseCapture();

        Content = root;
        UpdatePreview();
    }

    /// <summary>The live preview color from the current HSV/alpha state.</summary>
    public RgbaColor CurrentColor
    {
        get
        {
            var rgb = ColorConversions.HsvToRgb(_hsv);
            return rgb with { A = _alpha };
        }
    }

    public event Action<string>? Applied;
    public event Action? Cancelled;

    private void UpdateSvFromPoint(Point p)
    {
        _hsv = _hsv with
        {
            S = Math.Clamp(p.X / _svCanvas.Width, 0, 1),
            V = Math.Clamp(1 - p.Y / _svCanvas.Height, 0, 1)
        };
        UpdatePreview();
    }

    private void UpdateHueFromPoint(Point p)
    {
        _hsv = _hsv with { H = Math.Clamp(p.X / _hueCanvas.Width, 0, 1) * 360 };
        UpdatePreview();
    }

    internal void SetFromHex(string hex)
    {
        if (ThemeSettings.ParseColor(hex) is not { } color) return;
        _hsv = ColorConversions.RgbToHsv(color);
        _alpha = color.A;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var rgb = ColorConversions.HsvToRgb(_hsv);
        _svHueLayer.Fill = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        Canvas.SetLeft(_svThumb, _svCanvas.Width * _hsv.S - 7);
        Canvas.SetTop(_svThumb, _svCanvas.Height * (1 - _hsv.V) - 7);
        Canvas.SetLeft(_hueThumb, _hueCanvas.Width * (_hsv.H / 360) - 5);
        // S1244 suppressed: the slider is integral (Minimum 0, Maximum 255,
        // IsSnapToTickEnabled), so the compare is exact and terminates the
        // value-sync loop instead of re-entering it forever.
#pragma warning disable S1244 // integral slider values: exact compare terminates the loop
        if (_alphaSlider != null && _alphaSlider.Value != _alpha) _alphaSlider.Value = _alpha;
#pragma warning restore S1244

        _suppress = true;
        _hexBox.Text = ColorConversions.FormatHex(CurrentColor);
        _suppress = false;
    }

    private static Color HexToMediaColor(string hex)
        => ThemeSettings.ParseColor(hex) is { } c
            ? Color.FromArgb(c.A, c.R, c.G, c.B)
            : Colors.Black;
}
