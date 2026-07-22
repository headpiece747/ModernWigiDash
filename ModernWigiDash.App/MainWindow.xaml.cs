using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Core.Usb;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

public partial class MainWindow : Window, IWidgetContext
{
    private readonly WidgetPluginLoader _loader = new();
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly WigiDashHidDevice _usbDevice = new();
    private readonly DispatcherTimer _renderTimer = new();

    private ProfileLayout _profile = new();
    private PlacedWidgetInstance? _selectedWidget;

    // Mouse & Swipe Gesture Interaction State
    private bool _isMouseDown = false;
    private bool _isDraggingWidget = false;
    private Point _lastMousePos;
    private float _swipeStartX;
    private float _swipeStartY;
    private float _hwTouchStartX;
    private float _hwTouchStartY;
    private bool _isUpdatingInspector = false;

    public MainWindow()
    {
        InitializeComponent();

        // 1. Register Built-In ModernWigiDash Suite
        _loader.RegisterBuiltInPlugin(typeof(DigitalAnalogClockWidget));
        _loader.RegisterBuiltInPlugin(typeof(Aida64SensorWidget));
        _loader.RegisterBuiltInPlugin(typeof(HwInfoMonitorWidget));
        _loader.RegisterBuiltInPlugin(typeof(AudioVisualizerWidget));
        _loader.RegisterBuiltInPlugin(typeof(SpotifyControllerWidget));
        _loader.RegisterBuiltInPlugin(typeof(HotkeyButtonWidget));
        _loader.RegisterBuiltInPlugin(typeof(StopwatchTimerWidget));
        _loader.RegisterBuiltInPlugin(typeof(CryptoStockTickerWidget));
        _loader.RegisterBuiltInPlugin(typeof(PictureAndGifWidget));
        _loader.RegisterBuiltInPlugin(typeof(TwitchChatStreamWidget));
        _loader.RegisterBuiltInPlugin(typeof(WeatherForecastWidget));

        // 2. Populate Catalog UI
        ListCatalog.ItemsSource = _loader.RegisteredPlugins;

        // 3. Setup Default Profile Layout with 3 cool starter widgets
        SetupDefaultStarterLayout();
        RebuildPageTabsUI();

        // 4. Hook up USB Hardware physical touch events to Skia RouteTouch & hardware page swipe navigation
        _usbDevice.OnTouchEvent += (point, touchType) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (touchType == TouchEventType.TouchDown)
                {
                    _hwTouchStartX = point.X;
                    _hwTouchStartY = point.Y;
                }
                else if (touchType == TouchEventType.TouchUp && _profile.Pages.Count > 1)
                {
                    // Check side arrow taps on hardware screen
                    if (point.X <= 60 && point.Y >= 200 && point.Y <= 400 && _profile.ActivePageIndex > 0)
                    {
                        SwitchToPage(_profile.ActivePageIndex - 1);
                        return;
                    }
                    if (point.X >= 964 && point.Y >= 200 && point.Y <= 400 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                    {
                        SwitchToPage(_profile.ActivePageIndex + 1);
                        return;
                    }

                    // Check physical swipe left/right across hardware screen
                    float deltaX = point.X - _hwTouchStartX;
                    float deltaY = point.Y - _hwTouchStartY;

                    if (Math.Abs(deltaX) > 70 && Math.Abs(deltaY) < 80)
                    {
                        if (deltaX < -70 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                        {
                            // Physical swipe left -> next page
                            SwitchToPage(_profile.ActivePageIndex + 1);
                            return;
                        }
                        else if (deltaX > 70 && _profile.ActivePageIndex > 0)
                        {
                            // Physical swipe right -> previous page
                            SwitchToPage(_profile.ActivePageIndex - 1);
                            return;
                        }
                    }
                }

                _compositor.RouteTouch(_profile.ActivePage, point.X, point.Y, touchType);
                SkiaCanvas.InvalidateVisual();
            });
        };

        // 5. Start 60 FPS Skia Render Loop & Hardware Frame Streamer
        _renderTimer.Interval = TimeSpan.FromMilliseconds(16.67); // 60 FPS
        _renderTimer.Tick += (s, e) =>
        {
            _compositor.Compose(_profile.ActivePage, 60.0f, _profile.ActivePageIndex, _profile.Pages.Count);
            SkiaCanvas.InvalidateVisual();
            _usbDevice.SendFrameBuffer(_compositor.FrameBuffer);
            if (TxtUsbStatus.Text != _usbDevice.DeviceStatus)
            {
                TxtUsbStatus.Text = _usbDevice.DeviceStatus;
            }
        };
        _renderTimer.Start();

        // Update USB badge
        TxtUsbStatus.Text = _usbDevice.DeviceStatus;
        UpdateActiveCount();
        UpdateInspectorPanel();
    }

    private void SetupDefaultStarterLayout()
    {
        var page = _profile.ActivePage;
        page.Widgets.Clear();

        // Add Clock at top left
        PlaceWidgetOnCanvas("clock_modern", 25, 25, 408, 150);
        // Add AIDA64 at top right
        PlaceWidgetOnCanvas("aida64_panel", 460, 25, 408, 300);
        // Add Audio Visualizer at bottom
        PlaceWidgetOnCanvas("audio_visualizer", 25, 350, 843, 220);
    }

    private void PlaceWidgetOnCanvas(string pluginId, float x, float y, float width = -1, float height = -1)
    {
        var instance = _loader.CreateInstance(pluginId);
        if (instance == null) return;

        instance.InitializeAsync(this);

        var placed = new PlacedWidgetInstance
        {
            PluginId = pluginId,
            DisplayName = _loader.RegisteredPlugins.FirstOrDefault(p => p.PluginId == pluginId)?.DisplayName ?? pluginId,
            X = x,
            Y = y,
            Width = width > 0 ? width : instance.DefaultSize.Width,
            Height = height > 0 ? height : instance.DefaultSize.Height,
            ZIndex = _profile.ActivePage.Widgets.Count + 1,
            ActiveInstance = instance
        };

        _profile.ActivePage.Widgets.Add(placed);
        SelectWidget(placed);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void SelectWidget(PlacedWidgetInstance? widget)
    {
        _selectedWidget = widget;
        _compositor.SelectedWidget = widget;
        UpdateInspectorPanel();
        SkiaCanvas.InvalidateVisual();
    }

    private void UpdateActiveCount()
    {
        TxtActiveCount.Text = $"Active Widgets: {_profile.ActivePage.Widgets.Count}";
    }

    #region Skia Canvas Rendering & Mouse Interaction

    private void SkiaCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        _compositor.Compose(_profile.ActivePage, 60.0f, _profile.ActivePageIndex, _profile.Pages.Count);
        e.Surface.Canvas.DrawBitmap(_compositor.FrameBuffer, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));

        // Stream frame to USB hardware if physical device is connected
        _usbDevice.SendFrameBuffer(_compositor.FrameBuffer);
    }

    private void SkiaCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        var pos = e.GetPosition(SkiaCanvas);
        _lastMousePos = pos;
        _swipeStartX = (float)pos.X;
        _swipeStartY = (float)pos.Y;

        // Check if user clicked on side navigation arrows when multiple pages exist
        if (_profile.Pages.Count > 1)
        {
            if (pos.X <= 50 && pos.Y >= 250 && pos.Y <= 350 && _profile.ActivePageIndex > 0)
            {
                SwitchToPage(_profile.ActivePageIndex - 1);
                _isMouseDown = false;
                return;
            }
            if (pos.X >= 974 && pos.Y >= 250 && pos.Y <= 350 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
            {
                SwitchToPage(_profile.ActivePageIndex + 1);
                _isMouseDown = false;
                return;
            }
        }

        // Hit test against active widgets
        var hit = _compositor.HitTest(_profile.ActivePage, (float)pos.X, (float)pos.Y);
        SelectWidget(hit);

        if (hit != null && _compositor.IsEditMode)
        {
            _isDraggingWidget = true;
        }
        else if (hit != null && !_compositor.IsEditMode)
        {
            // Forward touch down
            _compositor.RouteTouch(_profile.ActivePage, (float)pos.X, (float)pos.Y, TouchEventType.TouchDown);
        }
    }

    private void SkiaCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown) return;
        var pos = e.GetPosition(SkiaCanvas);

        if (_isDraggingWidget && _selectedWidget != null && _compositor.IsEditMode)
        {
            float dx = (float)(pos.X - _lastMousePos.X);
            float dy = (float)(pos.Y - _lastMousePos.Y);

            _selectedWidget.X += dx;
            _selectedWidget.Y += dy;

            // Snap to grid if enabled
            if (_profile.ActivePage.SnapToGrid && _selectedWidget.X > 0 && _selectedWidget.Y > 0)
            {
                float grid = _profile.ActivePage.GridSpacingPx;
                if (grid > 0 && e.LeftButton == MouseButtonState.Pressed)
                {
                    // Snap gently or allow free-form
                }
            }

            _lastMousePos = pos;
            UpdateInspectorTransformsOnly();
            SkiaCanvas.InvalidateVisual();
        }
        else if (_selectedWidget != null && !_compositor.IsEditMode)
        {
            _compositor.RouteTouch(_profile.ActivePage, (float)pos.X, (float)pos.Y, TouchEventType.TouchMove);
        }
    }

    private void SkiaCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(SkiaCanvas);

        // Check for horizontal swipe gesture left/right
        float deltaX = (float)pos.X - _swipeStartX;
        float deltaY = (float)pos.Y - _swipeStartY;

        if (_profile.Pages.Count > 1 && !_isDraggingWidget && Math.Abs(deltaX) > 80 && Math.Abs(deltaY) < 70)
        {
            if (deltaX < -80 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
            {
                // Swiped Left -> Next Page
                SwitchToPage(_profile.ActivePageIndex + 1);
                _isMouseDown = false;
                return;
            }
            else if (deltaX > 80 && _profile.ActivePageIndex > 0)
            {
                // Swiped Right -> Previous Page
                SwitchToPage(_profile.ActivePageIndex - 1);
                _isMouseDown = false;
                return;
            }
        }

        if (_selectedWidget != null && !_compositor.IsEditMode && _isMouseDown)
        {
            _compositor.RouteTouch(_profile.ActivePage, (float)pos.X, (float)pos.Y, TouchEventType.TouchUp);
        }

        if (_isDraggingWidget && _selectedWidget != null && _profile.ActivePage.SnapToGrid)
        {
            float grid = Math.Max(10f, _profile.ActivePage.GridSpacingPx);
            _selectedWidget.X = (float)Math.Round(_selectedWidget.X / grid) * grid;
            _selectedWidget.Y = (float)Math.Round(_selectedWidget.Y / grid) * grid;
            UpdateInspectorTransformsOnly();
            SkiaCanvas.InvalidateVisual();
        }

        _isMouseDown = false;
        _isDraggingWidget = false;
    }

    #endregion

    #region Right Property Inspector Dynamic Binding

    private void UpdateInspectorPanel()
    {
        if (_selectedWidget == null)
        {
            PanelEmptyInspector.Visibility = Visibility.Visible;
            PanelActiveInspector.Visibility = Visibility.Collapsed;
            return;
        }

        _isUpdatingInspector = true;
        try
        {
            PanelEmptyInspector.Visibility = Visibility.Collapsed;
            PanelActiveInspector.Visibility = Visibility.Visible;

            TxtInspName.Text = _selectedWidget.DisplayName;
            TxtInspId.Text = $"Instance ID: {_selectedWidget.InstanceId}";

            TxtPosX.Text = $"{_selectedWidget.X:F0}";
            TxtPosY.Text = $"{_selectedWidget.Y:F0}";
            TxtWidth.Text = $"{_selectedWidget.Width:F0}";
            TxtHeight.Text = $"{_selectedWidget.Height:F0}";
            TxtZIndex.Text = $"{_selectedWidget.ZIndex}";
            TxtRotation.Text = $"{_selectedWidget.Rotation:F0}";
            SliderOpacity.Value = _selectedWidget.Opacity;
            TxtOpacityVal.Text = $"{(int)(_selectedWidget.Opacity * 100)}%";

            // Build dynamic custom property editors for the widget
            PanelCustomProperties.Children.Clear();
            if (_selectedWidget.ActiveInstance != null)
            {
                var type = _selectedWidget.ActiveInstance.GetType();
                foreach (var prop in type.GetProperties())
                {
                    var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
                    if (attr == null) continue;

                    var propPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                    propPanel.Children.Add(new TextBlock
                    {
                        Text = attr.DisplayName,
                        FontSize = 11,
                        Foreground = Brushes.White,
                        Margin = new Thickness(0, 0, 0, 4)
                    });

                    object? currentVal = prop.GetValue(_selectedWidget.ActiveInstance) ?? attr.DefaultValue;

                    if (attr.PropertyType == WidgetPropertyType.Choice && attr.Options.Length > 0)
                    {
                        var combo = new ComboBox
                        {
                            ItemsSource = attr.Options,
                            SelectedItem = currentVal?.ToString(),
                            Background = (Brush)FindResource("BgCard"),
                            Foreground = Brushes.White,
                            Padding = new Thickness(8, 4, 8, 4)
                        };
                        combo.SelectionChanged += (s, e) =>
                        {
                            if (combo.SelectedItem != null)
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, combo.SelectedItem.ToString());
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, combo.SelectedItem.ToString());
                            }
                        };
                        propPanel.Children.Add(combo);
                    }
                    else if (attr.PropertyType == WidgetPropertyType.Boolean)
                    {
                        var chk = new CheckBox
                        {
                            Content = "Enabled / Active",
                            IsChecked = currentVal is bool b && b,
                            Foreground = Brushes.White
                        };
                        chk.Checked += (s, e) => { prop.SetValue(_selectedWidget.ActiveInstance, true); _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, true); };
                        chk.Unchecked += (s, e) => { prop.SetValue(_selectedWidget.ActiveInstance, false); _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, false); };
                        propPanel.Children.Add(chk);
                    }
                    else
                    {
                        // Text or Number or Color
                        var txt = new TextBox { Text = currentVal?.ToString() ?? "" };
                        txt.TextChanged += (s, e) =>
                        {
                            if (_isUpdatingInspector) return;
                            string str = txt.Text;
                            if (prop.PropertyType == typeof(float) && float.TryParse(str, out float fVal))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, fVal);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, fVal);
                            }
                            else if (prop.PropertyType == typeof(int) && int.TryParse(str, out int iVal))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, iVal);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, iVal);
                            }
                            else if (prop.PropertyType == typeof(string))
                            {
                                prop.SetValue(_selectedWidget.ActiveInstance, str);
                                _selectedWidget.ActiveInstance.OnPropertyChanged(prop.Name, str);
                            }
                        };
                        propPanel.Children.Add(txt);
                    }

                    PanelCustomProperties.Children.Add(propPanel);
                }
            }
        }
        finally
        {
            _isUpdatingInspector = false;
        }
    }

    private void UpdateInspectorTransformsOnly()
    {
        if (_selectedWidget == null) return;
        _isUpdatingInspector = true;
        TxtPosX.Text = $"{_selectedWidget.X:F0}";
        TxtPosY.Text = $"{_selectedWidget.Y:F0}";
        TxtWidth.Text = $"{_selectedWidget.Width:F0}";
        TxtHeight.Text = $"{_selectedWidget.Height:F0}";
        _isUpdatingInspector = false;
    }

    private void Transform_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingInspector || _selectedWidget == null) return;

        if (float.TryParse(TxtPosX.Text, out float x)) _selectedWidget.X = x;
        if (float.TryParse(TxtPosY.Text, out float y)) _selectedWidget.Y = y;
        if (float.TryParse(TxtWidth.Text, out float w) && w > 20) _selectedWidget.Width = w;
        if (float.TryParse(TxtHeight.Text, out float h) && h > 20) _selectedWidget.Height = h;
        if (int.TryParse(TxtZIndex.Text, out int z)) _selectedWidget.ZIndex = z;
        if (float.TryParse(TxtRotation.Text, out float r)) _selectedWidget.Rotation = r % 360;

        SkiaCanvas.InvalidateVisual();
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedWidget != null && TxtOpacityVal != null)
        {
            _selectedWidget.Opacity = (float)SliderOpacity.Value;
            TxtOpacityVal.Text = $"{(int)(_selectedWidget.Opacity * 100)}%";
            SkiaCanvas.InvalidateVisual();
        }
    }

    private void BtnDeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget != null)
        {
            _profile.ActivePage.Widgets.Remove(_selectedWidget);
            SelectWidget(null);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    #endregion

    #region Catalog, Header, and Action Handlers

    private void TxtSearchCatalog_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = TxtSearchCatalog.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(query))
        {
            ListCatalog.ItemsSource = _loader.RegisteredPlugins;
        }
        else
        {
            ListCatalog.ItemsSource = _loader.RegisteredPlugins.Where(p =>
                p.DisplayName.ToLowerInvariant().Contains(query) ||
                p.Category.ToLowerInvariant().Contains(query)).ToList();
        }
    }

    private void BtnPlaceWidget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pluginId)
        {
            // Drop in center of screen
            PlaceWidgetOnCanvas(pluginId, 308, 150);
        }
    }

    private void RebuildPageTabsUI()
    {
        if (PanelPageTabs == null) return;
        PanelPageTabs.Children.Clear();
        for (int i = 0; i < _profile.Pages.Count; i++)
        {
            int pageIndex = i;
            var page = _profile.Pages[i];
            bool isActive = (pageIndex == _profile.ActivePageIndex);

            var container = new Grid { Margin = new Thickness(3, 0, 3, 0) };

            var btn = new Button
            {
                Content = $"📄 {page.PageName}",
                Padding = new Thickness(14, 6, _profile.Pages.Count > 1 ? 28 : 14, 6),
                Style = isActive ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button))
            };
            btn.Click += (s, e) => SwitchToPage(pageIndex);
            container.Children.Add(btn);

            if (_profile.Pages.Count > 1)
            {
                var closeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Foreground = isActive ? Brushes.White : (Brush)FindResource("TextSecondary"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand
                };
                closeBtn.Click += (s, e) => DeletePage(pageIndex);
                container.Children.Add(closeBtn);
            }

            PanelPageTabs.Children.Add(container);
        }
    }

    private void SwitchToPage(int index)
    {
        if (index < 0 || index >= _profile.Pages.Count) return;
        _profile.ActivePageIndex = index;
        RebuildPageTabsUI();
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void DeletePage(int index)
    {
        if (_profile.Pages.Count <= 1) return;
        var targetPage = _profile.Pages[index];
        if (targetPage.Widgets.Count > 0)
        {
            var res = MessageBox.Show($"Are you sure you want to delete '{targetPage.PageName}' containing {targetPage.Widgets.Count} widget(s)?", "Delete Page", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }

        _profile.Pages.RemoveAt(index);
        if (_profile.ActivePageIndex >= _profile.Pages.Count)
        {
            _profile.ActivePageIndex = _profile.Pages.Count - 1;
        }
        RebuildPageTabsUI();
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void BtnAddPage_Click(object sender, RoutedEventArgs e)
    {
        var newPage = new PageLayout { PageName = $"Page {_profile.Pages.Count + 1}" };
        _profile.Pages.Add(newPage);
        _profile.ActivePageIndex = _profile.Pages.Count - 1;
        RebuildPageTabsUI();
        SelectWidget(null);
        UpdateActiveCount();
        SkiaCanvas.InvalidateVisual();
    }

    private void ChkSnapToGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (_profile != null && ChkSnapToGrid != null)
        {
            _profile.ActivePage.SnapToGrid = ChkSnapToGrid.IsChecked == true;
            SkiaCanvas?.InvalidateVisual();
        }
    }

    private void ChkEditMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_compositor != null && ChkEditMode != null)
        {
            _compositor.IsEditMode = ChkEditMode.IsChecked == true;
            SkiaCanvas?.InvalidateVisual();
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "WigiDash Profile (*.json)|*.json", FileName = "MyWigiDashProfile.json" };
        if (dlg.ShowDialog() == true)
        {
            string json = JsonSerializer.Serialize(_profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            MessageBox.Show("Profile exported successfully!", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "WigiDash Profile (*.json)|*.json" };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                string json = File.ReadAllText(dlg.FileName);
                var loaded = JsonSerializer.Deserialize<ProfileLayout>(json);
                if (loaded != null)
                {
                    _profile = loaded;
                    RebuildPageTabsUI();
                    SelectWidget(null);
                    UpdateActiveCount();
                    SkiaCanvas.InvalidateVisual();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing profile: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Are you sure you want to clear all widgets from the current page?", "Confirm Clear", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _profile.ActivePage.Widgets.Clear();
            SelectWidget(null);
            UpdateActiveCount();
            SkiaCanvas.InvalidateVisual();
        }
    }

    #endregion

    #region IWidgetContext Implementation for Telemetry & Host Services

    public string GetSetting(string key, string defaultValue = "") => defaultValue;
    public void SetSetting(string key, string value) { }
    public void LogInfo(string message) => System.Diagnostics.Debug.WriteLine($"[WigiDash INFO] {message}");
    public void LogError(string message, Exception? ex = null) => System.Diagnostics.Debug.WriteLine($"[WigiDash ERROR] {message}: {ex}");
    public void RequestRender() => Dispatcher.InvokeAsync(() => SkiaCanvas?.InvalidateVisual());
    public bool TryGetSensorValue(string sensorId, out float value) { value = 50f; return true; }
    public string GetSensorFormattedString(string sensorId) => "50.0";

    #endregion
}