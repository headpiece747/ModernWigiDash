using System.Globalization;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Callbacks the inspector renderer needs from its host window. Every value
/// write-back funnels through <see cref="ApplyInspectorPropertyValue"/> — the
/// renderer never touches the widget model directly. Dialogs are host-side
/// (the renderer is dialog-free); the re-entrancy guard (suppression of
/// programmatic sets during a panel rebuild) is enforced at that funnel, so
/// no editor builder carries its own suppression check.
/// </summary>
internal sealed class InspectorCallbacks
{
    /// <summary>Resolves a named resource from the window/application (may return null).</summary>
    public required Func<string, object?> TryFindResource { get; init; }

    /// <summary>Writes an inspector value back into the widget and profile (host-side side effect).</summary>
    public required Action<PropertyInfo?, object> ApplyInspectorPropertyValue { get; init; }

    /// <summary>Opens the icon picker for a property with an <see cref="EditorKind.IconPicker"/> editor.</summary>
    public required Action<PropertyInfo, IWidgetEditorProvider, TextBox> ShowIconSelectorPopup { get; init; }

    /// <summary>Keeps a ComboBox dropdown inside the window's client area.</summary>
    public required Action<ComboBox> AttachDropdownWithinWindow { get; init; }

    /// <summary>Opens a file picker; returns the chosen path or null when cancelled.</summary>
    public required Func<string, string?, string?> BrowseFile { get; init; }

    /// <summary>Opens a folder picker; returns the chosen path or null when cancelled.</summary>
    public required Func<string, string?> BrowseFolder { get; init; }

    /// <summary>Commits a location-search pick (the picked label) to the
    /// selected widget through its IWidgetLocationSearch contract.</summary>
    public Action<GeocodeCandidate>? CommitLocationPick { get; init; }

    /// <summary>Saves a machine-local CalDAV password for a calendar feed (the
    /// feed editor's credential field routes here; the host writes through its
    /// DPAPI-backed store, never the profile).</summary>
    public Action<string, string>? SaveCalendarCredential { get; init; }
}

/// <summary>
/// Thin WPF mapper: renders <see cref="EditorDescription"/>s (produced by the
/// pure <see cref="InspectorModelBuilder"/>) into editor controls. All value
/// changes call <see cref="InspectorCallbacks.ApplyInspectorPropertyValue"/>
/// — one seam for every property type — and dialogs live behind the callbacks.
/// Widgets that need special editors implement <see cref="IWidgetEditorProvider"/>
/// (icon pickers, action-command paths); the renderer never branches on widget types.
/// </summary>
internal static class InspectorPanelRenderer
{
    /// <summary>
    /// Renders editors for every <see cref="EditorDescription"/> into
    /// <paramref name="target"/> (cleared by the caller).
    /// </summary>
    /// <param name="widget">The placed widget being inspected; action buttons
    /// and the icon picker resolve its active instance at interaction time.</param>
    /// <param name="descriptions">The editor descriptions to render, one per
    /// property or action (the pure model's output).</param>
    /// <param name="target">The element collection the editors render into
    /// (cleared by the caller).</param>
    /// <param name="callbacks">The host callbacks the editors and dialogs
    /// route through.</param>
    public static void Render(
        PlacedWidgetInstance widget,
        IReadOnlyList<EditorDescription> descriptions,
        UIElementCollection target,
        InspectorCallbacks callbacks)
    {
        var wiring = new ActionCommandWiring
        {
            Provider = widget.ActiveInstance as IWidgetEditorProvider
        };

        foreach (var desc in descriptions)
        {
            if (desc.IsAction)
            {
                target.Add(BuildActionButton(widget, desc, callbacks));
                continue;
            }

            AddPropertyPanel(widget, desc, target, callbacks, wiring);
        }

        WireActionCommandVisibility(wiring);
    }

    /// <summary>
    /// The action-command wiring state accumulated while building property
    /// editors: the action-type combo (when the widget names it), the
    /// action-command path panel (when the widget marks it), and the provider
    /// that decides whether the panel shows. <see cref="Render"/> wires the
    /// panel's visibility to the combo once both halves exist.
    /// </summary>
    private sealed class ActionCommandWiring
    {
        public required IWidgetEditorProvider? Provider { get; init; }
        public ComboBox? TypeCombo { get; set; }
        public StackPanel? CommandPanel { get; set; }
    }

    /// <summary>
    /// Builds one property's editor panel (title + the type-specific editor)
    /// and adds it to <paramref name="target"/>. Records the action-command
    /// wiring halves it produces in <paramref name="wiring"/>.
    /// </summary>
    private static void AddPropertyPanel(PlacedWidgetInstance widget, EditorDescription desc, UIElementCollection target, InspectorCallbacks callbacks, ActionCommandWiring wiring)
    {
        var propPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        propPanel.Children.Add(new TextBlock
        {
            Text = desc.DisplayName,
            FontSize = 11,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4)
        });

        BuildPropertyEditor(widget, desc, propPanel, callbacks, wiring);
        target.Add(propPanel);
    }

    /// <summary>
    /// Adds the type-specific editor for one property description into
    /// <paramref name="panel"/>. The choice and path branches record the
    /// action-command wiring halves in <paramref name="wiring"/>.
    /// </summary>
    private static void BuildPropertyEditor(PlacedWidgetInstance widget, EditorDescription desc, StackPanel panel, InspectorCallbacks callbacks, ActionCommandWiring wiring)
    {
        var provider = wiring.Provider;
        switch (desc.PropertyType)
        {
            case WidgetPropertyType.Choice when desc.Options.Count > 0:
                var combo = BuildOptionCombo(desc.Options, desc, callbacks);
                if (string.Equals(provider?.ActionCommandVisibilityChoicePropertyName, desc.Property.Name, StringComparison.Ordinal)) wiring.TypeCombo = combo;
                panel.Children.Add(combo);
                break;
            case WidgetPropertyType.Font:
                IReadOnlyList<WidgetPropertyOption> fontOptions = desc.Options.Count > 0
                ? desc.Options
                : FontHelper.GetAllFamilies().Select(family => new WidgetPropertyOption(family, family)).ToArray();
                panel.Children.Add(BuildOptionCombo(fontOptions, desc, callbacks));
                break;
            case WidgetPropertyType.Icon:
                panel.Children.Add(BuildIconEditor(widget, desc, callbacks));
                break;
            case WidgetPropertyType.Boolean:
                panel.Children.Add(BuildBooleanEditor(desc, callbacks));
                break;
            case WidgetPropertyType.Color:
                panel.Children.Add(BuildColorEditor(desc, callbacks));
                break;
            case WidgetPropertyType.Path:
                if (provider?.GetEditorKind(desc.Property) == EditorKind.ActionCommand)
                {
                    panel.Children.Add(BuildPathEditor(desc, callbacks,
                        "Select action folder", "Select action file or executable", "Programs and files (*.*)|*.*"));
                    wiring.CommandPanel = panel;
                }
                else
                {
                    panel.Children.Add(BuildPathEditor(desc, callbacks,
                        "Select image folder", "Select image file", "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"));
                }
                break;
            case WidgetPropertyType.SensorSelector:
                panel.Children.Add(BuildSensorSelector(desc, callbacks));
                break;
            case WidgetPropertyType.Text when provider?.GetEditorKind(desc.Property) == EditorKind.LocationSearch:
                if (widget.ActiveInstance is IWidgetLocationSearch search)
                {
                    panel.Children.Add(BuildLocationSearchEditor(desc, search, callbacks));
                    break;
                }
                // The widget advertises the location-search editor without
                // implementing the contract: degrade to the plain text
                // editor.
                panel.Children.Add(BuildTextEditor(desc, callbacks));
                break;
            case WidgetPropertyType.Text when provider?.GetEditorKind(desc.Property) == EditorKind.KeyCapture:
                panel.Children.Add(BuildKeyCaptureEditor(desc, callbacks).Editor);
                break;
            case WidgetPropertyType.Text when provider?.GetEditorKind(desc.Property) == EditorKind.CalendarFeeds:
                panel.Children.Add(BuildCalendarFeedEditor(desc, callbacks));
                break;
            default:
                // Text or Number
                panel.Children.Add(BuildTextEditor(desc, callbacks));
                break;
        }
    }

    /// <summary>
    /// Wires the action-command panel's visibility to the action-type combo
    /// once both halves exist (a widget that names its action-type choice and
    /// marks its command path): the panel shows or hides as the chosen action
    /// kind toggles.
    /// </summary>
    private static void WireActionCommandVisibility(ActionCommandWiring wiring)
    {
        if (wiring.TypeCombo is not { } combo || wiring.CommandPanel is not { } panel || wiring.Provider is not { } provider)
            return;

        void UpdateActionCommandVisibility()
        {
            string? selected = combo.SelectedValue?.ToString();
            panel.Visibility = provider.IsActionCommandVisible(selected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        combo.SelectionChanged += (_, _) => UpdateActionCommandVisibility();
        UpdateActionCommandVisibility();
    }

    private static Button BuildActionButton(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var btn = new Button
        {
            Content = desc.DisplayName,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var instance = widget.ActiveInstance;
        if (instance is IWidgetActionPresentationProvider presentation)
        {
            string? label = presentation.GetWidgetActionLabel(desc.Property.Name);
            if (!string.IsNullOrWhiteSpace(label)) btn.Content = label;

            if (presentation.IsWidgetActionActive(desc.Property.Name))
            {
                btn.Background = callbacks.TryFindResource("SuccessBackground") as Brush ?? btn.Background;
                btn.BorderBrush = callbacks.TryFindResource("SuccessBorder") as Brush ?? btn.BorderBrush;
                btn.BorderThickness = new Thickness(1);
                btn.Foreground = callbacks.TryFindResource("M3Primary") as Brush ?? Brushes.White;
            }
        }

        btn.Click += (_, _) =>
        {
            if (widget.ActiveInstance is IWidgetActionInvoker invoker)
                invoker.InvokeWidgetAction(desc.Property.Name);
        };
        return btn;
    }

    /// <summary>
    /// One option-combo builder for every choice-style editor: the choice,
    /// font, and sensor selectors all share this shape (ItemsSource /
    /// DisplayMemberPath / SelectedValuePath / funnel-routed write-back /
    /// dropdown clamp). The three call sites only differ in the option source.
    /// </summary>
    private static ComboBox BuildOptionCombo(IReadOnlyList<WidgetPropertyOption> options, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = nameof(WidgetPropertyOption.DisplayName),
            SelectedValuePath = nameof(WidgetPropertyOption.Value),
            SelectedValue = desc.CurrentValue?.ToString(),
            Padding = new Thickness(8, 4, 8, 4)
        };
        combo.SelectionChanged += (_, _) =>
        {
            string? selectedValue = combo.SelectedValue?.ToString();
            // Empty values are valid selections (e.g. the weather widget's
            // "Automatic (by ranking)" Location Match entry) — only a null
            // selection (nothing chosen) is skipped.
            if (selectedValue is not null)
                callbacks.ApplyInspectorPropertyValue(desc.Property, selectedValue);
        };
        callbacks.AttachDropdownWithinWindow(combo);
        return combo;
    }

    /// <summary>
    /// Icon picker row. The widget's <see cref="IWidgetEditorProvider"/> supplies
    /// the companion file-path property (cleared whenever the named icon changes)
    /// and the popup host; a widget without a provider gets a plain text editor.
    /// </summary>
    private static UIElement BuildIconEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var provider = widget.ActiveInstance as IWidgetEditorProvider;
        PropertyInfo? iconFileProp = provider?.GetIconFileCompanion(desc.Property);
        string seed = desc.CurrentValue?.ToString() ?? "";
        if (string.IsNullOrEmpty(seed)
            && iconFileProp != null
            && widget.ActiveInstance != null
            && iconFileProp.GetValue(widget.ActiveInstance) is string companionSeed
            && !string.IsNullOrEmpty(companionSeed))
        {
            seed = companionSeed;
        }
        var box = new TextBox { Text = seed };
        var btnBrowse = new Button { Content = "Browse\u2026", Padding = new Thickness(8, 2, 8, 2) };
        DockPanel.SetDock(btnBrowse, Dock.Right);
        btnBrowse.Click += (_, _) =>
        {
            if (provider == null) return;
            callbacks.ShowIconSelectorPopup(desc.Property, provider, box);
        };
        box.TextChanged += (_, _) =>
        {
            if (iconFileProp != null) callbacks.ApplyInspectorPropertyValue(iconFileProp, "");
            callbacks.ApplyInspectorPropertyValue(desc.Property, box.Text);
        };
        row.Children.Add(btnBrowse);
        row.Children.Add(box);
        return row;
    }

    private static CheckBox BuildBooleanEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var chk = new CheckBox
        {
            Content = desc.DisplayName,
            IsChecked = desc.CurrentValue is bool b && b,
            Foreground = Brushes.White
        };
        chk.Checked += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, true);
        chk.Unchecked += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, false);
        return chk;
    }

    /// <summary>Path editor: text box with Folder/File pickers. The action
    /// command and image-path editors differ only in the dialog title and file
    /// filter — one parameterized builder instead of two ~40-line copies.</summary>
    private static UIElement BuildPathEditor(EditorDescription desc, InspectorCallbacks callbacks, string folderTitle, string fileTitle, string fileFilter)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var txt = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
        txt.TextChanged += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, txt.Text);

        var btnFolder = new Button
        {
            Content = "Folder\u2026",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        DockPanel.SetDock(btnFolder, Dock.Right);
        btnFolder.Click += (_, _) =>
        {
            string? folder = callbacks.BrowseFolder(folderTitle);
            if (!string.IsNullOrEmpty(folder)) txt.Text = folder;
        };

        var btnFile = new Button
        {
            Content = "File\u2026",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        DockPanel.SetDock(btnFile, Dock.Right);
        btnFile.Click += (_, _) =>
        {
            string? file = callbacks.BrowseFile(fileTitle, fileFilter);
            if (!string.IsNullOrEmpty(file)) txt.Text = file;
        };

        row.Children.Add(btnFile);
        row.Children.Add(btnFolder);
        row.Children.Add(txt);
        return row;
    }

    private static UIElement BuildSensorSelector(EditorDescription desc, InspectorCallbacks callbacks)
    {
        if (desc.Options.Count == 0)
        {
            return new TextBlock
            {
                Text = "No sensors detected. Start LibreHardwareService, then reopen settings to pick a sensor.",
                FontSize = 11,
                Foreground = callbacks.TryFindResource("TextSecondary") as Brush ?? Brushes.White,
                TextWrapping = TextWrapping.Wrap
            };
        }

        return BuildOptionCombo(desc.Options, desc, callbacks);
    }

    private static UIElement BuildColorEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var editor = new ColorPickerEditor
        {
            Hex = desc.CurrentValue?.ToString() ?? ""
        };
        editor.Applied += hex => callbacks.ApplyInspectorPropertyValue(desc.Property, hex);
        return editor;
    }

    private static TextBox BuildTextEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var txt = new TextBox { Text = desc.CurrentValue?.ToString() ?? "" };
        txt.TextChanged += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, txt.Text);
        return txt;
    }

    /// <summary>
    /// The calendar feed editor: one row per feed (kind toggle, label, the
    /// kind's connection field(s), an enable checkbox, and a remove button) plus
    /// an "Add feed" button. Every mutation updates the live draft list and
    /// commits the whole list as a single <c>FeedsJson</c> string through the
    /// write-back funnel -- the model owns the serialize rule (incomplete feeds
    /// are dropped), so a half-entered row never persists a broken feed. The
    /// rows are rebuilt from the persisted value on every inspector refresh, so
    /// the editor is stateless across rebuilds.
    /// </summary>
    private static UIElement BuildCalendarFeedEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        List<CalendarFeedDraft> drafts = CalendarFeedEditorModel.Parse(desc.CurrentValue?.ToString()).ToList();

        void Commit() => callbacks.ApplyInspectorPropertyValue(desc.Property, CalendarFeedEditorModel.Serialize(drafts));

        var addBtn = new Button
        {
            Content = "Add feed",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0)
        };

        var host = new StackPanel();

        void RebuildRows()
        {
            // Drop every child (the rows AND the add button), then re-add the
            // rows followed by the button. The button is re-created each pass so
            // its handler always closes over the current draft list.
            host.Children.Clear();
            for (int i = 0; i < drafts.Count; i++)
            {
                int index = i;
                host.Children.Add(BuildFeedRow(drafts[index], index, () =>
                {
                    drafts.RemoveAt(index);
                    RebuildRows();
                    Commit();
                }, Commit, callbacks));
            }
            host.Children.Add(addBtn);
        }

        addBtn.Click += (_, _) =>
        {
            drafts.Add(new CalendarFeedDraft());
            RebuildRows();
            Commit();
        };

        RebuildRows();
        return host;
    }

    /// <summary>One feed row: the kind combo, the label box, the kind's
    /// connection field(s), a masked password field (CalDAV only, machine-local),
    /// the enable checkbox, and the remove button. Field edits update the bound
    /// draft and commit the whole list; the password commits through the
    /// credential seam, never into the persisted JSON.</summary>
    private static UIElement BuildFeedRow(CalendarFeedDraft draft, int index, Action remove, Action commit, InspectorCallbacks callbacks)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        // Kind + remove on the first line.
        var top = new DockPanel();
        var removeBtn = new Button { Content = "Remove", Padding = new Thickness(8, 2, 8, 2) };
        AutomationProperties.SetAutomationId(removeBtn, $"CalFeedRemove_{index}");
        DockPanel.SetDock(removeBtn, Dock.Right);
        removeBtn.Click += (_, _) => remove();
        var kindCombo = new ComboBox
        {
            ItemsSource = new[] { "ics", "caldav" },
            SelectedItem = draft.Kind,
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(kindCombo, $"CalFeedKind_{index}");
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is string kind)
            {
                draft.Kind = kind;
                commit();
            }
        };
        top.Children.Add(kindCombo);
        top.Children.Add(removeBtn);
        row.Children.Add(top);

        // Label.
        var labelBox = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(labelBox, $"CalFeedLabel_{index}");
        labelBox.Text = draft.Label;
        labelBox.TextChanged += (_, _) => { draft.Label = labelBox.Text; commit(); };
        row.Children.Add(labelBox);

        // Connection fields: swap between the .ics URL and the CalDAV triple as
        // the kind changes. A single content host keeps the row stable.
        var connHost = new ContentControl { Margin = new Thickness(0, 4, 0, 0) };
        void RebuildConn()
        {
            if (string.Equals(draft.Kind, "caldav", StringComparison.Ordinal))
            {
                var grid = new StackPanel();
                grid.Children.Add(BuildConnField("Server", draft.Server, v => { draft.Server = v; commit(); }, $"CalFeedServer_{index}"));
                grid.Children.Add(BuildConnField("Principal path", draft.PrincipalPath, v => { draft.PrincipalPath = v; commit(); }, $"CalFeedPrincipal_{index}"));
                grid.Children.Add(BuildConnField("Username", draft.Username, v => { draft.Username = v; commit(); }, $"CalFeedUsername_{index}"));
                // The password is machine-local: it never rides the persisted JSON
                // (the profile travels between machines), so it commits through
                // the credential seam instead of the draft serialize. A blank
                // feed id cannot be saved (the host refuses it).
                grid.Children.Add(BuildPasswordField(draft.FeedId, callbacks, $"CalFeedPassword_{index}"));
                connHost.Content = grid;
            }
            else
            {
                connHost.Content = BuildConnField(".ics URL", draft.Url, v => { draft.Url = v; commit(); }, $"CalFeedUrl_{index}");
            }
        }
        RebuildConn();
        kindCombo.SelectionChanged += (_, _) => RebuildConn();
        row.Children.Add(connHost);

        // Enable toggle.
        var enable = new CheckBox { Content = "Enabled", IsChecked = draft.Enabled, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(enable, $"CalFeedEnabled_{index}");
        enable.Checked += (_, _) => { draft.Enabled = true; commit(); };
        enable.Unchecked += (_, _) => { draft.Enabled = false; commit(); };
        row.Children.Add(enable);

        return row;
    }

    /// <summary>A labeled single-line field: a caption plus a text box that
    /// pushes its text through <paramref name="onEdit"/> as it changes. The
    /// handler is wired after the seed text is set, so the initial population
    /// does not fire a spurious commit.</summary>
    private static UIElement BuildConnField(string caption, string seed, Action<string> onEdit, string automationId)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        var box = new TextBox();
        AutomationProperties.SetAutomationId(box, automationId);
        box.Text = seed;
        box.TextChanged += (_, _) => onEdit(box.Text);
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>A masked CalDAV password field: a caption plus a
    /// <see cref="PasswordBox"/> that commits its text through the credential
    /// seam (machine-local, never the persisted JSON) as it changes. The box is
    /// not seeded (the store holds the secret, not the editor), so typing saves
    /// immediately and a rebuild shows an empty box with a "saved" hint.</summary>
    private static UIElement BuildPasswordField(string feedId, InspectorCallbacks callbacks, string automationId)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock { Text = "Password (stored on this machine)", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        var box = new PasswordBox();
        AutomationProperties.SetAutomationId(box, automationId);
        box.PasswordChanged += (_, _) =>
        {
            if (callbacks.SaveCalendarCredential is { } save && !string.IsNullOrEmpty(feedId))
                save(feedId, box.Password);
        };
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>
    /// The key-capture editor (the hotkey widget's global-hotkey chord): the
    /// chord's text box (typed chords commit through the single funnel like
    /// the plain text editor; the chord vocabulary validates them at
    /// registration time) plus the "Press keys..." capture. While capturing,
    /// the box's PreviewKeyDown routes the press through the pure
    /// <see cref="KeyCaptureModel"/> rules (the modifier order, the
    /// no-modifier refusal, the modifier-key-as-main-key refusal) and a
    /// recorded chord writes the box (the TextChanged commit is the single
    /// write path). Every press during capture is swallowed - a refused press
    /// never types into the box and corrupts the stored chord; the
    /// vocabulary's main keys (letters, digits, F1-F24) map to a name, a
    /// symbol, modifier, or numpad press stays armed (a numpad digit's name
    /// would spell the same chord as the number row but register a different
    /// virtual key), Escape cancels, and a focus loss cancels the capture.
    /// </summary>
    internal static (UIElement Editor, KeyCaptureModel Model) BuildKeyCaptureEditor(EditorDescription desc, InspectorCallbacks callbacks)
    {
        var model = new KeyCaptureModel(desc.CurrentValue?.ToString() ?? "");
        var box = new TextBox { Text = model.Chord };
        box.TextChanged += (_, _) => callbacks.ApplyInspectorPropertyValue(desc.Property, box.Text);

        var btn = new Button
        {
            Content = "Press keys...",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0)
        };
        btn.Click += (_, _) =>
        {
            model.BeginCapture();
            if (!box.Focus())
            {
                // A box that cannot take focus can never receive the press:
                // the armed capture would be a zombie (no LostFocus can fire),
                // so the failed focus cancels it.
                model.CancelCapture();
            }
        };

        box.PreviewKeyDown += (_, e) =>
        {
            if (!model.IsCapturing) return;
            Key key = KeyCaptureModel.ResolvePressKey(e.Key, e.SystemKey);
            if (key == Key.Escape)
            {
                e.Handled = true;
                model.CancelCapture();
                return;
            }
            string? name = KeyCaptureModel.ChordKeyName(key);
            if (name is null)
            {
                // A symbol, modifier, or numpad press: swallowed (the
                // capture stays armed, the box keeps its text).
                e.Handled = true;
                return;
            }
            bool captured = model.CaptureKey(
                name,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Windows));
            e.Handled = true; // every press during capture stays out of the box
            if (captured)
                box.Text = model.Chord; // the TextChanged commit is the write
        };
        box.LostFocus += (_, _) => model.CancelCapture();

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(btn, Dock.Right);
        row.Children.Add(btn);
        row.Children.Add(box);
        return (row, model);
    }

    /// <summary>
    /// The search-as-you-type Location editor: a TextBox with a results popup,
    /// debounced (300 ms), stale responses discarded by a version token. Enter or
    /// focus loss commits the typed text as the property value (the ambiguity
    /// gate then decides whether it may fetch); picking a result commits the
    /// candidate's label through <see cref="InspectorCallbacks.CommitLocationPick"/>.
    /// </summary>
    private static StackPanel BuildLocationSearchEditor(EditorDescription desc, IWidgetLocationSearch search, InspectorCallbacks callbacks)
    {
        // The box seeds from the Location label plus the last resolved
        // population's compact suffix ("New York, New York, United States ·
        // 8.4M") — the same shared formatter the search list's lines use, so
        // the field and the list can never disagree about a population. The
        // suffix is display-only: an empty label never seeds " · 8.4M" with no
        // label, and the base label is what commits and searches while the box
        // still holds the seeded text (a real user edit takes over verbatim).
        string baseLabel = desc.CurrentValue?.ToString() ?? "";
        string seed = LocationSearchModel.SeedText(baseLabel, search.CurrentPopulation);
        var box = new TextBox { Text = seed };
        var results = new ListBox
        {
            MaxHeight = 160,
            Visibility = Visibility.Collapsed,
            ItemTemplate = BuildCandidateLineTemplate()
        };
        var content = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = results
        };
        var popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = content
        };

        var version = new SearchVersionToken();
        bool popupPressed = false;
        bool syncingText = false;
        // A mouse press inside the popup (a pick gesture) steals focus from the
        // box BEFORE the ListBox processes the click — mark it so the LostFocus
        // commit is skipped (committing would overwrite the pending pick and
        // close the popup mid-gesture). Refocusing the box clears the mark.
        content.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => popupPressed = true), handledEventsToo: true);
        box.GotFocus += (_, _) => popupPressed = false;

        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            // The tick's query is the model's rule: the seed's population
            // suffix searches the base label while the box still holds the
            // seeded text (no real user edit yet).
            string query = LocationSearchModel.QueryFor(box.Text, seed, baseLabel);
            var (outcome, candidates) = await LocationSearchModel.RunSearchTickAsync(search, query, version);
            ApplySearchResults(results, popup, outcome, candidates);
        };
        box.TextChanged += (_, _) =>
        {
            if (syncingText) return; // a pick-sync must not restart the search debounce
            debounce.Stop();
            debounce.Start();
        };
        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is GeocodeCandidate picked)
            {
                popup.IsOpen = false;
                results.Visibility = Visibility.Collapsed;
                // Keep the box in sync with the pick so a later focus-loss
                // commit writes the picked label, not the stale typed query.
                syncingText = true;
                try { box.Text = picked.Label; }
                finally { syncingText = false; }
                callbacks.CommitLocationPick?.Invoke(picked);
            }
        };
        void CommitTypedText()
        {
            // The commit rule is the model's: the seeded suffix is
            // display-only — committing it verbatim would persist "label ·
            // 9.4k" and degrade the next resolution to a bare-name tie.
            string committed = LocationSearchModel.CommitText(box.Text, seed, baseLabel);
            callbacks.ApplyInspectorPropertyValue(desc.Property, committed);
            popup.IsOpen = false;
            results.Visibility = Visibility.Collapsed;
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitTypedText();
        };
        // Focus loss commits too: without it, typed-but-unfocused text would be
        // silently discarded by the next inspector refresh and the ambiguity
        // gate would never evaluate the typed name.
        box.LostFocus += (_, _) =>
        {
            // A click inside the popup is a pick in progress (an IsMouseOver
            // focus steal arrives without one, e.g. keyboard navigation): the
            // model vetoes the commit so the ListBox can finish the selection.
            if (!LocationSearchModel.ShouldCommitOnLostFocus(popupPressed, popup.IsOpen && popup.IsMouseOver)) return;
            CommitTypedText();
        };

        return new StackPanel { Children = { box, popup } };
    }

    /// <summary>
    /// The results list's item template: the candidate label plus a compact
    /// population suffix when the geocoder reported one ("Berlin, New
    /// Hampshire, United States · 9.4k") — never the record's ToString dump.
    /// </summary>
    private static DataTemplate BuildCandidateLineTemplate()
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new MultiBinding
        {
            Converter = new CandidateLineConverter(),
            Bindings = { new Binding(nameof(GeocodeCandidate.Label)), new Binding(nameof(GeocodeCandidate.Population)) }
        });
        text.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        text.SetValue(TextBlock.FontSizeProperty, 12.0);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2));
        template.VisualTree = text;
        return template;
    }

    /// <summary>
    /// Applies one tick decision to the results list and popup: Visible + open
    /// when candidates arrived, Collapsed + closed otherwise. A stale response
    /// (a newer tick owns the UI) touches nothing.
    /// </summary>
    internal static void ApplySearchResults(ListBox results, Popup popup, LocationSearchTick outcome, IReadOnlyList<GeocodeCandidate>? candidates)
    {
        if (outcome == LocationSearchTick.Stale) return;
        if (outcome == LocationSearchTick.NoSearch || candidates is null || candidates.Count == 0)
        {
            results.ItemsSource = null;
            results.Visibility = Visibility.Collapsed;
            popup.IsOpen = false;
            return;
        }

        results.ItemsSource = candidates;
        results.Visibility = Visibility.Visible;
        popup.IsOpen = true;
    }

    /// <summary>
    /// Formats one search result line: the candidate label plus a compact
    /// population suffix when the geocoder reported one ("Berlin, New
    /// Hampshire, United States · 9.4k"); the bare label when population is 0.
    /// </summary>
    internal sealed class CandidateLineConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            string label = values[0] as string ?? "";
            double population = values[1] is double p ? p : 0;
            return population > 0 ? $"{label} · {LocationSearchModel.FormatPopulation(population)}" : label;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
