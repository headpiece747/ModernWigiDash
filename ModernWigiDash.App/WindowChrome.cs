using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Window chrome: the dark DWM title bar (Windows 10 1809+/11 caption color)
/// applied to the main window and every owned dialog. Theme resources are the
/// ThemeManager's job; this module owns only the per-window chrome.
/// </summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    /// <summary>
    /// Enables the dark title bar and paints the caption to match the theme.
    /// Assigns the app icon when the window has none (owned dialogs).
    /// </summary>
    public static void ApplyDarkTitleBar(Window window, string captionHex)
    {
        if (window.Icon == null)
        {
            try
            {
                window.Icon = new BitmapImage(
                    new Uri("pack://application:,,,/Resources/Logo/logo.ico"));
            }
            catch (IOException)
            {
                // A missing icon resource must not crash dialog creation
                // (e.g. in test hosts without the app's pack resources).
            }
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Enable dark mode title bar (Windows 10 1809+)
        int darkMode = 1;
        int darkResult = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        if (darkResult != 0)
        {
            FileLog.Write($"[CHROME] DwmSetWindowAttribute (immersive dark mode) failed for '{window.Title}': 0x{darkResult:X8}");
        }
        // Set title bar background to match app theme (Windows 11+)
        var color = ThemeSettings.ParseColor(captionHex) ?? new RgbaColor(255, 0x0F, 0x11, 0x1A);
        int colorRef = (color.B << 16) | (color.G << 8) | color.R; // COLORREF (BBGGRR)
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }
}
