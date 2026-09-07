using System.Text;
using System.Windows.Media.Effects;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Theming;

/// <summary>
/// The theme-application module: one class that turns the current theme into (a) the application resources,
/// (b) the preview-shadow accent, (c) the per-window dark DWM title bar, and
/// (d) the applied-log line. Windows (main + dialogs) call <see cref="Apply"/>
/// and own none of that themselves. One implementation, no interface: a
/// seam with a single adapter is hypothetical (the repo's own rule).
/// </summary>
internal sealed class ThemeApplicator
{
    /// <summary>The named preview surface whose shadow must be re-applied on
    /// theme change — DropShadowEffect does not track DynamicResource.</summary>
    private const string PreviewFrameName = "PreviewFrame";

    /// <summary>The fingerprint of the theme last applied to the app
    /// resources; null until the first application.</summary>
    private string? _appliedFingerprint;

    /// <summary>The theme-category log (tag baked once).</summary>
    private readonly ModernWigiDash.Sdk.DiagLog _log = new("THEME", 1);

    /// <summary>
    /// Applies the current theme to <paramref name="window"/>. The app
    /// resources and the preview shadow are re-applied only when the theme
    /// changed since the last application; the DWM title bar is applied on
    /// every call (each new window needs its own chrome).
    /// </summary>
    public void Apply(Window window)
    {
        string fingerprint = Fingerprint(ThemeStore.Current);
        bool themeChanged = !string.Equals(fingerprint, _appliedFingerprint, StringComparison.Ordinal);
        if (themeChanged)
        {
            ThemeManager.ApplyToApplication();
            ReapplyPreviewShadow(window);
            _appliedFingerprint = fingerprint;
        }

        WindowChrome.ApplyDarkTitleBar(window, ThemeStore.Current.TitleBar);

        if (themeChanged)
        {
            var t = ThemeStore.Current;
            _log.Write($"Applied: TitleBar={t.TitleBar} AccentRed={t.AccentRed}");
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
        foreach (var prop in ThemeSettings.StringProperties)
        {
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
            RgbaColor? accent = PreviewShadowAccent(ThemeStore.Current);
            if (accent != null) shadow.Color = ThemeManager.ToMediaColor(accent.Value);
        }
    }
}
