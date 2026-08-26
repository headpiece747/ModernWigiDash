using System.Windows.Input;
using ModernWigiDash.App.Theming;

namespace ModernWigiDash.App.Dialogs;

/// <summary>
/// Settings hub dialog (ADR-0018): three groups built from
/// <see cref="SettingsModel"/> display facts. Appearance opens the existing
/// <see cref="ThemeDialog"/> as a nested modal; Behavior's close-behavior
/// radios write through to the window's commit seam the moment they are
/// checked (there is no Apply step: the radio write is the change, and the
/// window marks the profile dirty there); Profile's export/import buttons
/// route to the window's file flows. Closing the dialog (button, X, or
/// Escape) simply ends the hub - nothing is pending.
/// </summary>
internal sealed class SettingsDialog : Window
{
    private readonly ThemeApplicator _themeApplicator;
    private readonly SettingsModel _model = new();
    private readonly Action<string> _onCommitCloseBehavior;
    private readonly Action _onExportProfile;
    private readonly Action _onImportProfile;
    private readonly Dictionary<string, RadioButton> _radioByValue = [];

    /// <param name="owner">The owner window for modal centering.</param>
    /// <param name="themeApplicator">Applies the current theme to this
    /// window's chrome and to the theme editor it opens.</param>
    /// <param name="currentCloseBehavior">The raw persisted close-behavior
    /// value. The seed routes through
    /// <see cref="SettingsModel.CheckedCloseBehaviorFor"/>, so an absent or
    /// unknown value seeds the default radio.</param>
    /// <param name="onCommitCloseBehavior">Fires with the behavior value the
    /// moment a radio is checked. The window writes the profile and marks it
    /// dirty.</param>
    /// <param name="onExportProfile">Fires on the Profile group's export
    /// button (the window's SaveFileDialog + ProfileOps flow).</param>
    /// <param name="onImportProfile">Fires on the Profile group's import
    /// button (the window's OpenFileDialog + ProfileOps flow).</param>
    public SettingsDialog(
        Window owner,
        ThemeApplicator themeApplicator,
        string? currentCloseBehavior,
        Action<string> onCommitCloseBehavior,
        Action onExportProfile,
        Action onImportProfile)
    {
        _themeApplicator = themeApplicator;
        _onCommitCloseBehavior = onCommitCloseBehavior;
        _onExportProfile = onExportProfile;
        _onImportProfile = onImportProfile;

        Title = "Settings";
        Width = 460;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        Background = Application.Current.Resources["BgPanel"] as Brush ?? Brushes.Black;
        FontFamily = Application.Current.Resources["PrimaryFont"] as FontFamily ?? SystemFonts.MessageFontFamily;
        SourceInitialized += (_, _) => _themeApplicator.Apply(this);

        Content = BuildUi(currentCloseBehavior);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private Grid BuildUi(string? currentCloseBehavior)
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // groups

        var title = new TextBlock
        {
            Text = "Settings",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Application.Current.Resources["TextPrimary"] as Brush ?? Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        var fields = new StackPanel();

        // The sections bind to the model's group order by index; the order
        // itself (Appearance, Behavior, Profile) is pinned in
        // SettingsModelTests, so a reordered model fails the gate.
        var sections = new (SettingsModel.Group Group, UIElement Content)[]
        {
            (SettingsModel.Groups[0], BuildAppearanceGroup()),
            (SettingsModel.Groups[1], BuildBehaviorGroup(currentCloseBehavior)),
            (SettingsModel.Groups[2], BuildProfileGroup())
        };
        foreach (var (group, content) in sections)
        {
            fields.Children.Add(BuildGroupHeader(group));
            fields.Children.Add(content);
        }

        scroll.Content = fields;
        root.Children.Add(scroll);
        return root;
    }

    private static StackPanel BuildGroupHeader(SettingsModel.Group group)
    {
        var header = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = group.Title.ToUpperInvariant(),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Application.Current.Resources["M3Primary"] as Brush ?? Brushes.White
        });
        header.Children.Add(new TextBlock
        {
            Text = group.Description,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["TextSecondary"] as Brush ?? Brushes.White,
            Margin = new Thickness(0, 2, 0, 4)
        });
        return header;
    }

    private UIElement BuildAppearanceGroup()
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(BuildRowLabel("Theme colors"));
        row.Children.Add(BuildRowHint("The chrome's palette, edited in its own dialog."));
        var button = new Button
        {
            Content = "Customize theme colors...",
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        // The theme dialog owns its full lifetime; this hub just opens it.
        button.Click += (_, _) => new ThemeDialog(this, _themeApplicator).ShowDialog();
        row.Children.Add(button);
        return row;
    }

    private UIElement BuildBehaviorGroup(string? currentCloseBehavior)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(BuildRowLabel("Close the window"));
        row.Children.Add(BuildRowHint("Applies to the X button, Alt+F4, and minimize."));

        // The seed happens before the subscription: a radio checked on open
        // fires its Checked event with no handler attached yet, so opening
        // the hub commits nothing - the radio write is the change.
        var checkedValue = _model.CheckedCloseBehaviorFor(currentCloseBehavior);
        foreach (var option in SettingsModel.CloseBehaviors)
        {
            var radio = new RadioButton
            {
                Content = option.Label,
                GroupName = "CloseBehavior",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Application.Current.Resources["TextPrimary"] as Brush ?? Brushes.White,
                Margin = new Thickness(0, 6, 0, 0)
            };
            _radioByValue[option.Value] = radio;
            row.Children.Add(radio);
            row.Children.Add(BuildRowHint(option.Description, leftIndent: 20));
            radio.IsChecked = string.Equals(option.Value, checkedValue, StringComparison.Ordinal);
            radio.Checked += (_, _) => _onCommitCloseBehavior(option.Value);
        }

        return row;
    }

    private UIElement BuildProfileGroup()
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(BuildRowLabel("Profile file"));
        row.Children.Add(BuildRowHint(
            "The close behavior above travels with the exported file; an imported file without one keeps yours."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var export = new Button
        {
            Content = "Export profile...",
            Width = 150,
            Margin = new Thickness(0, 0, 8, 0)
        };
        export.Click += (_, _) => _onExportProfile();
        var import = new Button
        {
            Content = "Import profile...",
            Width = 150
        };
        import.Click += (_, _) => _onImportProfile();
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        row.Children.Add(buttons);
        return row;
    }

    private static TextBlock BuildRowLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Application.Current.Resources["TextPrimary"] as Brush ?? Brushes.White
        };

    private static TextBlock BuildRowHint(string text, double leftIndent = 0)
        => new()
        {
            Text = text,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["TextSecondary"] as Brush ?? Brushes.White,
            Margin = new Thickness(leftIndent, 2, 0, 4)
        };

    internal IEnumerable<T> FindVisualChildren<T>() where T : DependencyObject
        => FindVisualChildren<T>(this);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }
}
