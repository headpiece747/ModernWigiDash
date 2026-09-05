using System.Windows.Input;
using ModernWigiDash.App.Controls;
using ModernWigiDash.App.Theming;

namespace ModernWigiDash.App.Dialogs;

/// <summary>
/// Settings hub dialog (ADR-0018): three groups built from
/// <see cref="SettingsModel"/> display facts. Appearance opens the existing
/// <see cref="ThemeDialog"/> as a nested modal and hosts the page-background
/// picker (the active page's canvas color, relocated from the page-tab
/// strip), writing through on the moment a color is picked; Behavior's
/// close-behavior radios write through to the window's commit seam the moment they are
/// checked, and the Start-with-Windows checkbox (ADR-0019) writes or deletes
/// the app's HKCU Run entry the same way (the registry is the single source
/// of truth, so the checkbox seeds from the entry's presence); there is no
/// Apply step anywhere - the control write is the change, and the window
/// marks the profile dirty where the profile is what changed. Profile's
/// export/import buttons route to the window's file flows. Closing the
/// dialog (button, X, or Escape) simply ends the hub - nothing is pending.
/// </summary>
internal sealed class SettingsDialog : Window
{
    private readonly ThemeApplicator _themeApplicator;
    private readonly SettingsModel _model = new();
    private readonly ISettingsHubHost _host;

    /// <param name="owner">The owner window for modal centering.</param>
    /// <param name="themeApplicator">Applies the current theme to this
    /// window's chrome and to the theme editor it opens.</param>
    /// <param name="host">The named host seam (the ADR-0008 image): the hub
    /// reads its five open-time seeds once from
    /// <see cref="ISettingsHubHost.Seed"/> and routes every write-through and
    /// file flow through the host's commit members. The close-behavior seed
    /// routes through <see cref="SettingsModel.CheckedCloseBehaviorFor"/>, so
    /// an absent or unknown value seeds the default radio; every other seed
    /// is the host's persisted state at open time.</param>
    public SettingsDialog(
        Window owner,
        ThemeApplicator themeApplicator,
        ISettingsHubHost host)
    {
        _themeApplicator = themeApplicator;
        _host = host;
        var seed = host.Seed;

        Title = "Settings";
        Width = 460;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        // Track the resource key instead of capturing the brush object: ApplyToApplication
        // reassigns the dictionary entry on every theme apply, so an open dialog must
        // repaint in place instead of waiting to be rebuilt.
        SetResourceReference(BackgroundProperty, "BgPanel");
        FontFamily = Application.Current.Resources["PrimaryFont"] as FontFamily ?? SystemFonts.MessageFontFamily;
        SourceInitialized += (_, _) => _themeApplicator.Apply(this);

        Content = BuildUi(seed.CloseBehavior, seed.Autostart, seed.KillSwitch, seed.AhkInterpreterPath, seed.PageBackground, seed.MinimizeToTrayOnStartup);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private Grid BuildUi(string? currentCloseBehavior, bool currentAutostart, bool currentKillSwitch, string currentAhkPath, string currentPageBackground, bool currentMinimizeToTray)
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // groups

        var title = new TextBlock
        {
            Text = "Settings",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(title);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        var fields = new StackPanel();

        // The sections bind to the model's group order by index; the order
        // itself (Appearance, Behavior, Profile) is pinned in
        // SettingsModelTests, so a reordered model fails the gate.
        var sections = new (SettingsModel.Group Group, UIElement Content)[]
        {
            (SettingsModel.Groups[0], BuildAppearanceGroup(currentPageBackground)),
            (SettingsModel.Groups[1], BuildBehaviorGroup(currentCloseBehavior, currentAutostart, currentKillSwitch, currentAhkPath, currentMinimizeToTray)),
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
        var groupTitle = new TextBlock
        {
            Text = group.Title.ToUpperInvariant(),
            FontSize = 12,
            FontWeight = FontWeights.Bold
        };
        groupTitle.SetResourceReference(TextBlock.ForegroundProperty, "M3Primary");
        header.Children.Add(groupTitle);
        var groupDescription = new TextBlock
        {
            Text = group.Description,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 4)
        };
        groupDescription.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        header.Children.Add(groupDescription);
        return header;
    }

    private UIElement BuildAppearanceGroup(string currentPageBackground)
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

        // The page-background row (the former strip swatch, relocated here):
        // the active page's canvas color, written through the moment a color
        // is picked - the pick is the change, like the other rows.
        row.Children.Add(BuildRowLabel("Page background", topMargin: 12));
        row.Children.Add(BuildRowHint(
            "The canvas background color behind the widgets on the active page."));
        var pageBgEditor = new ColorPickerEditor();
        pageBgEditor.Hex = currentPageBackground;
        pageBgEditor.Applied += _host.CommitPageBackground;
        row.Children.Add(pageBgEditor);
        return row;
    }

    private UIElement BuildBehaviorGroup(string? currentCloseBehavior, bool currentAutostart, bool currentKillSwitch, string currentAhkPath, bool currentMinimizeToTray)
    {
#pragma warning disable S125 // layout-vs-routing split documentation, not commented-out code
        // The Behavior group's rows are pure layout over SettingsModel facts;
        // each control's commit wiring is a separate concern (the WireBehaviorRow
        // overloads), so "what the group looks like" and "what each control
        // writes" read as two short units instead of one interleaved block.
#pragma warning restore S125
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
                Margin = new Thickness(0, 6, 0, 0)
            };
            radio.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimary");
            row.Children.Add(radio);
            row.Children.Add(BuildRowHint(option.Description, leftIndent: 20));
            radio.IsChecked = string.Equals(option.Value, checkedValue, StringComparison.Ordinal);
            WireBehaviorRow(radio, () => _host.CommitCloseBehavior(option.Value));
        }

        row.Children.Add(BuildRowLabel("Start with Windows", topMargin: 12));
        row.Children.Add(BuildRowHint(
            "Launches the app at sign-in with the window minimized; the display streams either way."));
        var autostart = BuildBehaviorCheckBox("Start with Windows", currentAutostart);
        row.Children.Add(autostart);
        WireBehaviorRow(autostart, enabled => _host.CommitAutostart(enabled));

        // The kill-switch row (ADR-0019): like the autostart checkbox, the
        // check is the change - the window persists the machine-local
        // setting and re-runs the idempotent registration pass. Off (the
        // default, the vendor parity) keeps the global-hotkey integration
        // live; checked kills it for games that flag background input as
        // cheat software. The seed precedes the subscription, so opening
        // the hub with the switch tripped commits nothing.
        row.Children.Add(BuildRowLabel("Kill Switch", topMargin: 12));
        row.Children.Add(BuildRowHint(
            "Kills the global-hotkey integration (hotkey registration + AHK script spawning) for games that flag background input as cheat software. Every other action keeps running from a tap."));
        var killSwitch = BuildBehaviorCheckBox("Kill Switch", currentKillSwitch);
        row.Children.Add(killSwitch);
        WireBehaviorRow(killSwitch, tripped => _host.CommitKillSwitch(tripped));

        // The minimize-to-tray-on-startup row (ADR-0019): like the autostart
        // checkbox, the check is the change - the window persists the
        // machine-local flag and the next launch opens hidden behind the
        // tray icon. Off (the default) opens the window normally. The seed
        // precedes the subscription, so opening the hub with the flag set
        // commits nothing.
        row.Children.Add(BuildRowLabel("Minimize to tray on startup", topMargin: 12));
        row.Children.Add(BuildRowHint(
            "The app opens hidden behind the tray icon; the display streams either way. Use the tray icon to show or quit."));
        var minimizeToTray = BuildBehaviorCheckBox("Minimize to tray on startup", currentMinimizeToTray);
        row.Children.Add(minimizeToTray);
        WireBehaviorRow(minimizeToTray, enabled => _host.CommitMinimizeToTrayOnStartup(enabled));

        // The AutoHotkey row (ADR-0019): the interpreter the Run AHK Script
        // action spawns - a machine-local path (app_settings.json, never
        // the profile), nothing bundled or auto-detected. The box commits
        // on focus loss; Browse routes to the window's file dialog (a
        // chosen path commits through the same seam and rides back into
        // the box, so the displayed path cannot drift from the persisted
        // one; a cancel leaves the box untouched).
        row.Children.Add(BuildRowLabel("AutoHotkey", topMargin: 12));
        row.Children.Add(BuildRowHint(
            "The user's AutoHotkey interpreter (autohotkey.exe) for the Run AHK Script action; blank leaves the action refusing."));
        var ahkRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var ahkPath = new TextBox { Text = currentAhkPath, Width = 260 };
        var browse = new Button
        {
            Content = "Browse...",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        ahkRow.Children.Add(ahkPath);
        ahkRow.Children.Add(browse);
        row.Children.Add(ahkRow);
        WireBehaviorRow(ahkPath, browse, _host);

        return row;
    }

    /// <summary>
    /// One Behavior-group checkbox row: the label/hint-free control only (the
    /// label and hint are added by the caller around it). The seed value is
    /// applied before any commit handler attaches, so opening the hub with
    /// the setting already set commits nothing.
    /// </summary>
    private static CheckBox BuildBehaviorCheckBox(string content, bool seeded)
    {
        var box = new CheckBox
        {
            Content = content,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
            IsChecked = seeded
        };
        box.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimary");
        return box;
    }

    /// <summary>
    /// Wires a close-behavior radio to its single host commit: the seed
    /// (IsChecked) is applied by the caller BEFORE this runs, so a radio
    /// checked on open fires its Checked event with no handler attached yet
    /// and commits nothing.
    /// </summary>
    private static void WireBehaviorRow(RadioButton radio, Action commit)
    {
        radio.Checked += (_, _) => commit();
    }

    /// <summary>
    /// Wires a Behavior checkbox to its single host commit (both edges: a
    /// check commits true, an uncheck commits false). The seed (IsChecked)
    /// is applied by the caller BEFORE this runs, so opening the hub with
    /// the setting already set commits nothing.
    /// </summary>
    private static void WireBehaviorRow(CheckBox box, Action<bool> commit)
    {
        box.Checked += (_, _) => commit(true);
        box.Unchecked += (_, _) => commit(false);
    }

    /// <summary>
    /// Wires the AutoHotkey row: the box commits its trimmed text on focus
    /// loss; Browse routes to the host's file dialog and a chosen path rides
    /// back into the box (a cancel leaves the box untouched).
    /// </summary>
    private static void WireBehaviorRow(TextBox ahkPath, Button browse, ISettingsHubHost host)
    {
        ahkPath.LostFocus += (_, _) => host.CommitAhkInterpreter(ahkPath.Text?.Trim() ?? "");
        browse.Click += (_, _) =>
        {
            string? chosen = host.BrowseAhkInterpreter();
            if (chosen is null) return; // a cancel leaves the box (and the setting) untouched
            ahkPath.Text = chosen; // the committed path rides back into the box
        };
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
        export.Click += (_, _) => _host.ExportProfile();
        var import = new Button
        {
            Content = "Import profile...",
            Width = 150
        };
        import.Click += (_, _) => _host.ImportProfile();
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        row.Children.Add(buttons);
        return row;
    }

    private static TextBlock BuildRowLabel(string text, double topMargin = 0)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, topMargin, 0, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return label;
    }

    private static TextBlock BuildRowHint(string text, double leftIndent = 0)
    {
        var hint = new TextBlock
        {
            Text = text,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(leftIndent, 2, 0, 4)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return hint;
    }

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
