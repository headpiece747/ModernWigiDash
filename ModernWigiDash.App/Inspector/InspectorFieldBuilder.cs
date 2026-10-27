using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Shared UI builder helpers for inspector editors.
/// </summary>
internal static class InspectorFieldBuilder
{
    /// <summary>
    /// Builds a labeled single-line text field: a caption plus a text box that
    /// pushes its text through <paramref name="onEdit"/> as it changes.
    /// </summary>
    public static UIElement BuildConnField(string caption, string seed, Action<string> onEdit, string automationId)
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
}
