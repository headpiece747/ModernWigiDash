using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ModernWigiDash.Core.Theming;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.Theming;

/// <summary>
/// The theme-application seam: one module that turns the current
/// <see cref="ThemeSettings.Theme"/> into (a) the application resources,
/// (b) the preview-shadow accent, (c) the per-window dark DWM title bar, and
/// (d) the applied-log line. Windows (main + dialogs) call <see cref="Apply"/>
/// and own none of that themselves.
/// </summary>
public interface IThemeApplicator
{
    /// <summary>
    /// Applies the current theme to <paramref name="window"/>. The app
    /// resources and the preview shadow are re-applied only when the theme
    /// changed since the last application; the DWM title bar is applied on
    /// every call (each new window needs its own chrome).
    /// </summary>
    void Apply(Window window);
}

/// <inheritdoc cref="IThemeApplicator"/>
public sealed class ThemeApplicator : IThemeApplicator
{
    /// <summary>The named preview surface whose shadow must be re-applied on
    /// theme change — DropShadowEffect does not track DynamicResource.</summary>
    private const string PreviewFrameName = "PreviewFrame";

    /// <summary>The fingerprint of the theme last applied to the app
    /// resources; null until the first application.</summary>
    private string? _appliedFingerprint;

    public void Apply(Window window)
    {
        string fingerprint = Fingerprint(ThemeSettings.Theme);
        bool themeChanged = fingerprint != _appliedFingerprint;
        if (themeChanged)
        {
            ThemeManager.ApplyToApplication();
            ReapplyPreviewShadow(window);
            _appliedFingerprint = fingerprint;
        }

        WindowChrome.ApplyDarkTitleBar(window, ThemeSettings.Theme.TitleBar);

        if (themeChanged)
        {
            var t = ThemeSettings.Theme;
            FileLog.Write($"[THEME] Applied: TitleBar={t.TitleBar} AccentRed={t.AccentRed}");
        }
    }

    /// <summary>
    /// The pure preview-shadow rule: the accent color the preview frame's
    /// DropShadowEffect should use — the theme's accent, or nothing when the
    /// hex is invalid (the shadow then keeps its current color).
    /// </summary>
    internal static RgbaColor? PreviewShadowAccent(ThemeSettings theme)
        => ThemeSettings.ParseColor(theme.AccentRed);

    /// <summary>
    /// Pure change signal for the applied theme: the concatenation of every
    /// themeable hex value. Unchanged theme, unchanged fingerprint — so the
    /// applicator re-applies the app resources only when the theme actually
    /// changed.
    /// </summary>
    internal static string Fingerprint(ThemeSettings theme)
    {
        var sb = new StringBuilder();
        foreach (var prop in typeof(ThemeSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(string)) continue;
            sb.Append(prop.Name).Append('=').Append((string?)prop.GetValue(theme)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>DropShadowEffect does not track DynamicResource — re-derive the
    /// accent color and reassign it whenever the theme changed.</summary>
    private static void ReapplyPreviewShadow(Window window)
    {
        if (window.FindName(PreviewFrameName) is FrameworkElement preview &&
            preview.Effect is DropShadowEffect shadow)
        {
            RgbaColor? accent = PreviewShadowAccent(ThemeSettings.Theme);
            if (accent != null) shadow.Color = ThemeManager.ToMediaColor(accent.Value);
        }
    }
}
