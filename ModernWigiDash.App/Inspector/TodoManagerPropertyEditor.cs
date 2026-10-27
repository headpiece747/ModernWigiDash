using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Todo;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Custom property editor for the task manager (<see cref="EditorKind.TodoManager"/>).
/// Renders draft task rows (checkbox, up/down, remove, title, category) and an "Add task" button.
/// </summary>
internal sealed class TodoManagerPropertyEditor : ICustomPropertyEditor
{
    public EditorKind Kind => EditorKind.TodoManager;

    public UIElement BuildEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        var rows = new TodoTaskEditorRows(
            desc.CurrentValue?.ToString(),
            json =>
            {
                if (widget.ActiveInstance is not ITaskCommitter committer) return;
                committer.CommitTasks(json);
                callbacks.ApplyInspectorPropertyValue(desc.Property, json);
            });

        var addBtn = new Button
        {
            Content = "Add task",
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
                host.Children.Add(BuildTodoTaskRow(rows, index, RebuildRows));
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

    private static UIElement BuildTodoTaskRow(TodoTaskEditorRows rows, int index, Action rebuild)
    {
        var draft = rows.Drafts[index];
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var top = new DockPanel();
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(btnPanel, Dock.Right);

        var upBtn = new Button
        {
            Content = "▲",
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 0, 0, 0),
            IsEnabled = index > 0,
        };
        AutomationProperties.SetAutomationId(upBtn, $"TodoUp_{index}");
        upBtn.Click += (_, _) =>
        {
            rows.MoveUp(index);
            rebuild();
        };

        var downBtn = new Button
        {
            Content = "▼",
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 0, 0, 0),
            IsEnabled = index < rows.Drafts.Count - 1,
        };
        AutomationProperties.SetAutomationId(downBtn, $"TodoDown_{index}");
        downBtn.Click += (_, _) =>
        {
            rows.MoveDown(index);
            rebuild();
        };

        var removeBtn = new Button
        {
            Content = "Remove",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
        };
        AutomationProperties.SetAutomationId(removeBtn, $"TodoRemove_{index}");
        removeBtn.Click += (_, _) =>
        {
            rows.Remove(index);
            rebuild();
        };

        btnPanel.Children.Add(upBtn);
        btnPanel.Children.Add(downBtn);
        btnPanel.Children.Add(removeBtn);

        var check = new CheckBox
        {
            Content = "Done",
            IsChecked = draft.IsDone,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        };
        AutomationProperties.SetAutomationId(check, $"TodoDone_{index}");
        check.Checked += (_, _) => rows.SetDone(index, true);
        check.Unchecked += (_, _) => rows.SetDone(index, false);

        top.Children.Add(check);
        top.Children.Add(btnPanel);
        row.Children.Add(top);

        row.Children.Add(InspectorFieldBuilder.BuildConnField("Title", draft.Title, v => rows.SetTitle(index, v), $"TodoTitle_{index}"));
        row.Children.Add(InspectorFieldBuilder.BuildConnField("Category (optional)", draft.Category, v => rows.SetCategory(index, v), $"TodoCategory_{index}"));

        return row;
    }
}
