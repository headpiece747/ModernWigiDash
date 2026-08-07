using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// MainWindow partial: IModernWigiDashContext host-contract implementation.
/// </summary>
public partial class MainWindow
{
    #region IModernWigiDashContext Implementation for Telemetry & Host Services

    public void LogInfo(string message) => FileLog.Write($"[Display INFO] {message}");
    public void LogError(string message, Exception? ex = null) => FileLog.Write($"[Display ERROR] {message}{(ex != null ? $": {ex}" : "")}");
    public void RequestRender() => Dispatcher.InvokeAsync(() => SkiaCanvas?.InvalidateVisual());

    public void RequestInspectorRefresh()
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateInspectorPanel();
            return;
        }

        _ = Dispatcher.InvokeAsync(UpdateInspectorPanel);
    }

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
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                FontFamily = TryFindResource("PrimaryFont") as FontFamily ?? FontFamily,
                Background = TryFindResource("BgPanel") as Brush ?? TryFindResource("PanelBackground") as Brush ?? Brushes.Black,
                Foreground = TryFindResource("TextPrimary") as Brush ?? Brushes.White
            };
            window.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(window, ThemeSettings.Theme.TitleBar);

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
            if (TryFindResource("AccentButton") is Style accentStyle) open.Style = accentStyle;

            open.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(verificationUri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogError("Unable to open the Twitch authorization page", ex);
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

        if (Dispatcher.CheckAccess()) ShowDialog();
        else Dispatcher.Invoke(ShowDialog);
    }

    public void CloseDeviceAuthorization()
    {
        void CloseDialog()
        {
            _deviceAuthorizationWindow?.Close();
            _deviceAuthorizationWindow = null;
        }

        if (Dispatcher.CheckAccess()) CloseDialog();
        else Dispatcher.Invoke(CloseDialog);
    }

    #endregion
}
