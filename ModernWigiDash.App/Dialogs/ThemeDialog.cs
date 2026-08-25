using System.Windows.Input;
using ModernWigiDash.App.Controls;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Dialogs;

/// <summary>
/// Theme customization dialog: edits the chrome theme colors (outside the
/// widget canvas) grouped by category, with hex validation and apply/reset.
/// Extracted from MainWindow so the dialog owns its entire UI lifetime. The
/// decision rules (entries, validity verdict, apply, reset) live in
/// <see cref="ThemeDraft"/>; this window builds the editors and forwards.
/// </summary>
internal sealed class ThemeDialog : Window
{
    private readonly ThemeApplicator _themeApplicator;
    private readonly ThemeDraft _draft;
    private readonly List<(string Key, ColorPickerEditor Editor)> _entries = [];
    private Button _btnApply = null!;

    /// <param name="owner">Owner window for modal centering.</param>
    /// <param name="themeApplicator">Applies the current <see cref="ThemeSettings.Theme"/>
    /// (resources, preview shadow, title bar, log).</param>
    public ThemeDialog(Window owner, ThemeApplicator themeApplicator)
    {
        _themeApplicator = themeApplicator;
        _draft = new ThemeDraft();

        Title = "🎨 Theme Customization";
        Width = 440;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        Background = Application.Current.Resources["BgPanel"] as Brush ?? Brushes.Black;
        FontFamily = Application.Current.Resources["PrimaryFont"] as FontFamily ?? SystemFonts.MessageFontFamily;
        SourceInitialized += (_, _) => _themeApplicator.Apply(this);

        Content = BuildUi();
        RefreshApplyState();
    }

    private Grid BuildUi()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // fields
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

        var title = new TextBlock
        {
            Text = "Chrome Theme — colors outside the widget canvas",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Application.Current.Resources["TextPrimary"] as Brush ?? Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        var fields = new StackPanel();

        string currentGroup = "";
        foreach (var entry in _draft.Entries)
        {
            if (!string.Equals(entry.Group, currentGroup, StringComparison.Ordinal))
            {
                currentGroup = entry.Group;
                fields.Children.Add(new TextBlock
                {
                    Text = entry.Group.ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Application.Current.Resources["M3Primary"] as Brush ?? Brushes.White,
                    Margin = new Thickness(0, 8, 0, 6)
                });
            }

            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var label = new TextBlock
            {
                Text = entry.FriendlyName,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Application.Current.Resources["TextSecondary"] as Brush ?? Brushes.White,
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = $"{entry.FriendlyName} ({entry.Name})"
            };
            var hint = new TextBlock
            {
                Text = entry.Description,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["TextSecondary"] as Brush ?? Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var editor = new ColorPickerEditor { Hex = entry.Hex };
            editor.Changed += () =>
            {
                _draft.UpdateHex(entry.Name, editor.Hex);
                RefreshApplyState();
            };
            row.Children.Add(label);
            row.Children.Add(hint);
            row.Children.Add(editor);
            fields.Children.Add(row);
            _entries.Add((entry.Name, editor));
        }
        scroll.Content = fields;
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(buttons, 2);
        var btnReset = new Button { Content = "Reset", Margin = new Thickness(0, 0, 8, 0) };
        var btnCancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        _btnApply = new Button { Content = "Apply", Style = Application.Current.Resources["AccentButton"] as Style };

        btnReset.Click += (_, _) =>
        {
            _draft.ResetToDefaults();
            foreach (var (key, editor) in _entries)
                editor.Hex = _draft.HexFor(key);
            RefreshApplyState(); // programmatic Hex sets never raise Changed — re-arm Apply explicitly
        };

        btnCancel.Click += (_, _) => Close();

        _btnApply.Click += (_, _) => ApplyFromDialog();

        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _btnApply.IsEnabled)
            {
                ApplyFromDialog();
                e.Handled = true;
            }
        };

        buttons.Children.Add(btnReset);
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(_btnApply);
        root.Children.Add(buttons);
        return root;
    }

    private void RefreshApplyState()
        => _btnApply.IsEnabled = _draft.IsValid;

    private void ApplyFromDialog()
    {
        _draft.ApplyToSettings();
        if (!ThemeSettings.Save())
        {
            MessageBox.Show("Could not write app_theme.json next to the app. The colors will apply for this session only.",
                            "Theme Save Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _themeApplicator.Apply(this);
        Close();
    }

    internal bool ApplyIsEnabledForTest => _btnApply.IsEnabled;

    internal ThemeDraft DraftForTest => _draft;

    internal IEnumerable<T> FindVisualChildren<T>() where T : DependencyObject
        => FindVisualChildren<T>(this);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
