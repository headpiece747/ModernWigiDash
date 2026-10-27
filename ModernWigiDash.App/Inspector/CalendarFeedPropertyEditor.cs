using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Custom property editor for calendar feeds (<see cref="EditorKind.CalendarFeeds"/>).
/// Renders feed rows (kind toggle, label, connection info, password, enable checkbox, remove)
/// and an "Add feed" button.
/// </summary>
internal sealed class CalendarFeedPropertyEditor : ICustomPropertyEditor
{
    public EditorKind Kind => EditorKind.CalendarFeeds;

    public UIElement BuildEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var rows = new CalendarFeedEditorRows(
            desc.CurrentValue?.ToString(),
            commit => callbacks.ApplyInspectorPropertyValue(desc.Property, commit),
            callbacks.SaveCalendarCredential);

        var addBtn = new Button
        {
            Content = "Add feed",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0)
        };

        var host = new StackPanel();

        void RebuildRows()
        {
            host.Children.Clear();
            for (int i = 0; i < rows.Drafts.Count; i++)
            {
                int index = i;
                host.Children.Add(BuildFeedRow(rows, index, RebuildRows));
            }
            host.Children.Add(addBtn);
        }

        addBtn.Click += (_, _) =>
        {
            rows.Add();
            RebuildRows();
        };

        RebuildRows();
        return host;
    }

    private static UIElement BuildFeedRow(CalendarFeedEditorRows rows, int index, Action rebuild)
    {
        var draft = rows.Drafts[index];
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        // Kind + remove on the first line.
        var top = new DockPanel();
        var removeBtn = new Button { Content = "Remove", Padding = new Thickness(8, 2, 8, 2) };
        AutomationProperties.SetAutomationId(removeBtn, $"CalFeedRemove_{index}");
        DockPanel.SetDock(removeBtn, Dock.Right);
        removeBtn.Click += (_, _) =>
        {
            rows.Remove(index);
            rebuild();
        };
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
                rows.SetKind(index, kind);
        };
        top.Children.Add(kindCombo);
        top.Children.Add(removeBtn);
        row.Children.Add(top);

        // Label.
        var labelBox = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(labelBox, $"CalFeedLabel_{index}");
        labelBox.Text = draft.Label;
        labelBox.TextChanged += (_, _) => rows.SetLabel(index, labelBox.Text);
        row.Children.Add(labelBox);

        // Connection fields: swap between the .ics URL and the CalDAV triple as
        // the kind changes. A single content host keeps the row stable.
        var connHost = new ContentControl { Margin = new Thickness(0, 4, 0, 0) };
        void RebuildConn()
        {
            if (string.Equals(draft.Kind, "caldav", StringComparison.Ordinal))
            {
                var grid = new StackPanel();
                grid.Children.Add(InspectorFieldBuilder.BuildConnField("Server", draft.Server, v => rows.SetServer(index, v), $"CalFeedServer_{index}"));
                grid.Children.Add(InspectorFieldBuilder.BuildConnField("Principal path", draft.PrincipalPath, v => rows.SetPrincipalPath(index, v), $"CalFeedPrincipal_{index}"));
                grid.Children.Add(InspectorFieldBuilder.BuildConnField("Username", draft.Username, v => rows.SetUsername(index, v), $"CalFeedUsername_{index}"));
                grid.Children.Add(BuildPasswordField(index, rows, $"CalFeedPassword_{index}"));
                connHost.Content = grid;
            }
            else
            {
                connHost.Content = InspectorFieldBuilder.BuildConnField(".ics URL", draft.Url, v => rows.SetUrl(index, v), $"CalFeedUrl_{index}");
            }
        }
        RebuildConn();
        kindCombo.SelectionChanged += (_, _) => RebuildConn();
        row.Children.Add(connHost);

        // Enable toggle.
        var enable = new CheckBox { Content = "Enabled", IsChecked = draft.Enabled, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(enable, $"CalFeedEnabled_{index}");
        enable.Checked += (_, _) => rows.SetEnabled(index, true);
        enable.Unchecked += (_, _) => rows.SetEnabled(index, false);
        row.Children.Add(enable);

        return row;
    }

    private static UIElement BuildPasswordField(int index, CalendarFeedEditorRows rows, string automationId)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock { Text = "Password (stored on this machine)", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        var box = new PasswordBox();
        AutomationProperties.SetAutomationId(box, automationId);
        box.PasswordChanged += (_, _) => rows.SavePassword(index, box.Password);
        panel.Children.Add(box);
        return panel;
    }
}
