using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// Small host dialogs the window used to own inline: the text prompt (page
/// rename) and the device-authorization window (Twitch device login). Owns the
/// authorization window's lifetime tracking; the window just forwards calls.
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
