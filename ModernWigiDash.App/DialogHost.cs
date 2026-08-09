using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// Small host dialogs the window used to own inline: the text prompt (page
/// rename), the device-authorization window (Twitch device login), and the
/// inspector's icon picker. Owns the authorization window's lifetime tracking;
/// the window just forwards calls.
/// </summary>
public sealed class DialogHost
{
    private readonly Window _owner;
    private readonly Func<string, object?> _tryFindResource;
    private readonly Action<string, Exception?> _logError;
    private Window? _deviceAuthorizationWindow;

    public DialogHost(Window owner, Func<string, object?> tryFindResource, Action<string, Exception?> logError)
    {
        _owner = owner;
        _tryFindResource = tryFindResource;
        _logError = logError;
    }

    /// <summary>
    /// Modal text prompt; returns the entered text or null when cancelled.
    /// </summary>
    public string? PromptForText(string title, string label, string initialValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = _owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = _tryFindResource("BgPanel") as Brush ?? Brushes.Black,
            FontFamily = _tryFindResource("PrimaryFont") as FontFamily ?? SystemFonts.MessageFontFamily
        };
        dialog.SourceInitialized += (_, _) => WindowChrome.ApplyDarkTitleBar(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = _tryFindResource("TextPrimary") as Brush ?? Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(labelBlock);

        var box = new TextBox { Text = initialValue };
        Grid.SetRow(box, 1);
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(buttons, 2);
        var btnCancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var btnOk = new Button { Content = "OK", Style = _tryFindResource("AccentButton") as Style, IsDefault = true };
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnOk);
        root.Children.Add(buttons);

        dialog.Content = root;
        box.Focus();
        box.SelectAll();

        string? result = null;
        btnOk.Click += (_, _) =>
        {
            result = box.Text;
            dialog.DialogResult = true;
        };
        btnCancel.Click += (_, _) => dialog.DialogResult = false;

        dialog.ShowDialog();
        return result;
    }

    /// <summary>Themed confirm dialog: true when the user confirmed, false on cancel.</summary>
    public bool Confirm(string title, string message)
    {
        var dialog = CreateMessageDialog(title, message, isConfirm: true);
        bool confirmed = false;
        WireMessageButton(dialog, () => confirmed = true);
        dialog.ShowDialog();
        return confirmed;
    }

    /// <summary>Themed info dialog (single OK button).</summary>
    public void Info(string title, string message)
    {
        var dialog = CreateMessageDialog(title, message, isConfirm: false);
        WireMessageButton(dialog, () => { });
        dialog.ShowDialog();
    }

    /// <summary>Themed error dialog (single OK button). The body intentionally
    /// mirrors <see cref="Info"/> — the kind is expressed by the caller's
    /// title; the dialog chrome is identical.</summary>
#pragma warning disable S4144 // Info and Error share the chrome by design
    public void Error(string title, string message)
    {
        var dialog = CreateMessageDialog(title, message, isConfirm: false);
        WireMessageButton(dialog, () => { });
        dialog.ShowDialog();
    }
#pragma warning restore S4144

    /// <summary>Builds the themed message-dialog shell: the message block and
    /// an OK button (plus a Cancel button for confirmations). The button
    /// actions are wired by <see cref="WireMessageButton"/>.</summary>
    private Window CreateMessageDialog(string title, string message, bool isConfirm)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = _owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = _tryFindResource("BgPanel") as Brush ?? Brushes.Black,
            FontFamily = _tryFindResource("PrimaryFont") as FontFamily ?? SystemFonts.MessageFontFamily
        };
        dialog.SourceInitialized += (_, _) => WindowChrome.ApplyDarkTitleBar(dialog, ThemeSettings.Theme.TitleBar);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = _tryFindResource("TextPrimary") as Brush ?? Brushes.White
        };
        root.Children.Add(messageBlock);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(buttons, 1);

        if (isConfirm)
        {
            var btnCancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            btnCancel.Click += (_, _) => dialog.DialogResult = false;
            buttons.Children.Add(btnCancel);
        }

        var btnOk = new Button { Content = "OK", Style = _tryFindResource("AccentButton") as Style, IsDefault = true };
        btnOk.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(btnOk);
        root.Children.Add(buttons);

        dialog.Content = root;
        return dialog;
    }

    /// <summary>Attaches the confirm action to the dialog's OK button (the
    /// button labels are fixed in <see cref="CreateMessageDialog"/>).</summary>
    private static void WireMessageButton(Window dialog, Action onConfirm)
    {
        if (dialog.Content is not Grid root || root.Children.Count < 2 || root.Children[1] is not StackPanel buttons)
            return;

        foreach (var child in buttons.Children)
        {
            if (child is Button btn && btn.Content as string == "OK")
            {
                btn.Click += (_, _) => onConfirm();
                return;
            }
        }
    }

    /// <summary>
    /// Modal icon picker for an inspector icon property: a searchable Griddy
    /// icon grid plus a custom-SVG browse button (copied into the icons folder).
    /// Reflection-agnostic — it receives the current value (named icon or
    /// custom file path) and returns the chosen value; the caller owns the
    /// write-back. Returns null when cancelled.
    /// </summary>
    public string? ShowIconPicker(string title, string currentValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 620,
            Owner = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = _tryFindResource("BgPanel") as Brush ?? _tryFindResource("PanelBackground") as Brush ?? Brushes.Black,
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
            Foreground = _tryFindResource("TextSecondary") as Brush ?? Brushes.Gray,
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
            Style = _tryFindResource("AccentButton") as Style
        };
        Grid.SetColumn(selectedName, 0);
        Grid.SetColumn(select, 1);
        footer.Children.Add(selectedName);
        footer.Children.Add(select);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        var accentBrush = _tryFindResource("AccentRed") as Brush ?? Brushes.Red;
        string chosen = currentValue ?? "";
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
                        cell.Content = new Path
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
                    cell.BorderBrush = accentBrush;
                cell.Click += (_, _) =>
                {
                    UpdateSelected(name);
                    foreach (var child in grid.Children.OfType<Button>())
                        child.BorderBrush = Brushes.Transparent;
                    cell.BorderBrush = accentBrush;
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
            chip.Text = $"Custom: {relative}";
            UpdateSelected(relative);
        };

        string? result = null;
        select.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(chosen)) return;
            result = chosen;
            dialog.DialogResult = true;
        };

        if (!string.IsNullOrWhiteSpace(currentValue) && !GriddyIcons.Contains(currentValue))
            chip.Text = $"Custom: {currentValue}";
        selectedName.Text = currentValue ?? "";
        RenderGrid();
        dialog.Content = root;
        dialog.ShowDialog();
        return result;
    }

    /// <summary>
    /// Non-modal device-authorization window (Twitch device flow): shows the
    /// user code and verification URL. Replaces any previously shown window.
    /// </summary>
    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt)
    {
        void ShowDialog()
        {
            _deviceAuthorizationWindow?.Close();

            var window = new Window
            {
                Title = $"ModernWigiDash - {serviceName} Login",
                Width = 430,
                SizeToContent = SizeToContent.Height,
                Owner = _owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = _tryFindResource("PrimaryFont") as FontFamily ?? SystemFonts.MessageFontFamily,
                Background = _tryFindResource("BgPanel") as Brush ?? _tryFindResource("PanelBackground") as Brush ?? Brushes.Black,
                Foreground = _tryFindResource("TextPrimary") as Brush ?? Brushes.White
            };
            window.SourceInitialized += (_, _) => WindowChrome.ApplyDarkTitleBar(window, ThemeSettings.Theme.TitleBar);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = $"Authorize {serviceName} in your browser",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new TextBlock
            {
                Text = "The browser should open automatically. If it does not, open the verification URL below and enter this code:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var code = new TextBox
            {
                Text = userCode,
                IsReadOnly = true,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(code);
            root.Children.Add(new TextBlock
            {
                Text = verificationUri.AbsoluteUri,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = $"This code expires at {expiresAt.LocalDateTime:t}.",
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var open = new Button { Content = "Open Twitch", Padding = new Thickness(12, 5, 12, 5) };
            var copy = new Button { Content = "Copy code", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
            var close = new Button { Content = "Cancel", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
            if (_tryFindResource("AccentButton") is Style accentStyle) open.Style = accentStyle;

            open.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(verificationUri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _logError("Unable to open the Twitch authorization page", ex);
                }
            };
            copy.Click += (_, _) => Clipboard.SetText(userCode);
            close.Click += (_, _) => window.Close();

            buttons.Children.Add(open);
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            root.Children.Add(buttons);
            window.Content = root;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_deviceAuthorizationWindow, window)) _deviceAuthorizationWindow = null;
            };
            _deviceAuthorizationWindow = window;
            window.Show();
        }

        if (_owner.Dispatcher.CheckAccess()) ShowDialog();
        else _owner.Dispatcher.Invoke(ShowDialog);
    }

    /// <summary>Closes the device-authorization window if one is showing.</summary>
    public void CloseDeviceAuthorization()
    {
        void CloseDialog()
        {
            _deviceAuthorizationWindow?.Close();
            _deviceAuthorizationWindow = null;
        }

        if (_owner.Dispatcher.CheckAccess()) CloseDialog();
        else _owner.Dispatcher.Invoke(CloseDialog);
    }
}
