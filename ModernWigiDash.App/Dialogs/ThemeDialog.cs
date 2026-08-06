using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App.Dialogs;

/// <summary>
/// Theme customization dialog: edits the chrome theme colors (outside the
/// widget canvas) grouped by category, with hex validation and apply/reset.
/// Extracted from MainWindow so the dialog owns its entire UI lifetime.
/// </summary>
public sealed class ThemeDialog : Window
{
    private readonly Action _applyTheme;
    private readonly Action<Window, string> _applyDarkTitleBar;
    private readonly List<(string Key, TextBox Box)> _entries = [];
    private Button _btnApply = null!;

    /// <param name="owner">Owner window for modal centering.</param>
    /// <param name="applyTheme">Applies the current <see cref="ThemeSettings.Theme"/> to the app resources.</param>
    /// <param name="applyDarkTitleBar">Applies the dark DWM title bar to a window.</param>
    public ThemeDialog(Window owner, Action applyTheme, Action<Window, string> applyDarkTitleBar)
    {
        _applyTheme = applyTheme;
        _applyDarkTitleBar = applyDarkTitleBar;

        Title = "🎨 Theme Customization";
        Width = 440;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        Background = (Brush)Application.Current.Resources["BgPanel"];
        FontFamily = (FontFamily)Application.Current.Resources["PrimaryFont"];
        SourceInitialized += (_, _) => _applyDarkTitleBar(this, ThemeSettings.Theme.TitleBar);

        Content = BuildUi();
        Validate();
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
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        var fields = new StackPanel();

        var props = typeof(ThemeSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .OrderBy(p => ThemeSettings.Groups.TryGetValue(p.Name, out var group) ? group : p.Name)
            .ThenBy(p => p.Name);

        string currentGroup = "";
        foreach (var prop in props)
        {
            string group = ThemeSettings.Groups.TryGetValue(prop.Name, out var grp) ? grp : "Other";
            if (group != currentGroup)
            {
                currentGroup = group;
                fields.Children.Add(new TextBlock
                {
                    Text = group.ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources["M3Primary"],
                    Margin = new Thickness(0, 8, 0, 6)
                });
            }

            string current = (string?)prop.GetValue(ThemeSettings.Theme) ?? "#000000";
            string friendly = ThemeSettings.FriendlyName(prop.Name);
            string desc = ThemeSettings.Descriptions.TryGetValue(prop.Name, out var d) ? d : "";

            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var label = new TextBlock
            {
                Text = friendly,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = $"{friendly} ({prop.Name})"
            };
            var hint = new TextBlock
            {
                Text = desc,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            var box = new TextBox { Text = current };
            row.Children.Add(label);
            row.Children.Add(hint);
            row.Children.Add(box);
            fields.Children.Add(row);
            _entries.Add((prop.Name, box));
        }
        scroll.Content = fields;
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(buttons, 2);
        var btnReset = new Button { Content = "Reset", Margin = new Thickness(0, 0, 8, 0) };
        var btnCancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) };
        _btnApply = new Button { Content = "Apply", Style = (Style)Application.Current.Resources["AccentButton"] };

        foreach (var (_, box) in _entries)
        {
            box.TextChanged += (_, _) => Validate();
            box.LostFocus += (_, _) => Validate();
        }

        btnReset.Click += (_, _) =>
        {
            var defaults = new ThemeSettings();
            foreach (var (key, box) in _entries)
                box.Text = (string?)defaults.GetType().GetProperty(key)?.GetValue(defaults) ?? "#000000";
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

    private void Validate()
    {
        bool valid = true;
        Brush borderBrush = (Brush)Application.Current.Resources["BorderBrush"];
        foreach (var (_, box) in _entries)
        {
            bool ok = ThemeSettings.ParseColor(box.Text) != null;
            box.BorderBrush = ok ? borderBrush : Brushes.Red;
            box.ToolTip = ok ? null : "Enter a hex color like #RRGGBB or #AARRGGBB";
            if (!ok) valid = false;
        }
        _btnApply.IsEnabled = valid;
    }

    private void ApplyFromDialog()
    {
        foreach (var (key, box) in _entries)
        {
            string value = box.Text.Trim();
            if (ThemeSettings.ParseColor(value) != null)
                ThemeSettings.Theme.GetType().GetProperty(key)?.SetValue(ThemeSettings.Theme, value);
        }
        if (!ThemeSettings.Save())
        {
            MessageBox.Show("Could not write app_theme.json next to the app. The colors will apply for this session only.",
                            "Theme Save Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _applyTheme();
        Close();
    }
}
