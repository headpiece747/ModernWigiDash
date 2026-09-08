using System.Windows.Automation;
using ModernWigiDash.App.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// The shared chrome for the small themed dialogs: the window shell (fixed-width,
/// centered on the owner, non-resizable, themed background/font, dark DWM title bar)
/// and the right-aligned OK/Cancel button row. One spelling of the button row means
/// the OK/Cancel AutomationIds and the accent-button style cannot drift between the
/// text prompt and the message dialogs. <see cref="DialogHost"/> composes this for its
/// four small dialogs; the icon picker and device-authorization windows keep their own
/// bespoke footers and do not route through here.
/// </summary>
internal sealed class DialogChrome
{
    private readonly Window _owner;
    private readonly ThemeApplicator _themeApplicator;
    private readonly Func<string, object?> _tryFindResource;

    public DialogChrome(Window owner, ThemeApplicator themeApplicator, Func<string, object?> tryFindResource)
    {
        _owner = owner;
        _themeApplicator = themeApplicator;
        _tryFindResource = tryFindResource;
    }

    /// <summary>Builds the themed dialog window shell: fixed-width, centered on the
    /// owner, non-resizable, themed background/font, and the dark DWM title bar.</summary>
    public Window CreateWindow(string title, double width)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = _owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = _tryFindResource("BgPanel") as Brush ?? Brushes.Black,
            FontFamily = _tryFindResource("PrimaryFont") as FontFamily ?? SystemFonts.MessageFontFamily
        };
        dialog.SourceInitialized += (_, _) => _themeApplicator.Apply(dialog);
        return dialog;
    }

    /// <summary>
    /// Builds the right-aligned OK/Cancel button row as a ready-to-place panel. The OK
    /// button is the default (accent-styled); the Cancel button is present only when
    /// <paramref name="withCancel"/> is set. Returns the panel plus the buttons so the
    /// caller wires the actions directly — no post-hoc button search. The AutomationIds
    /// are spelled once here, so every dialog's OK and Cancel carry the same ids the
    /// tests click.
    /// </summary>
    public (Panel Row, Button Ok, Button? Cancel) BuildButtonRow(bool withCancel)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        Button? cancel = null;
        if (withCancel)
        {
            cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            AutomationProperties.SetAutomationId(cancel, "BtnPromptCancel");
            buttons.Children.Add(cancel);
        }

        var ok = new Button { Content = "OK", Style = _tryFindResource("AccentButton") as Style, IsDefault = true };
        AutomationProperties.SetAutomationId(ok, "BtnPromptOk");
        buttons.Children.Add(ok);

        return (buttons, ok, cancel);
    }
}
