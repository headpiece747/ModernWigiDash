using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Custom property editor for global hotkey chord key capture (<see cref="EditorKind.KeyCapture"/>).
/// Renders a chord text box and a "Press keys..." button backed by <see cref="KeyCaptureModel"/>.
/// </summary>
internal sealed class KeyCapturePropertyEditor : ICustomPropertyEditor
{
    public EditorKind Kind => EditorKind.KeyCapture;

    public UIElement BuildEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks)
    {
        return BuildKeyCaptureEditor(desc, callbacks).Editor;
    }

    /// <summary>
    /// Builds the key capture editor tuple (Editor element and underlying model).
    /// </summary>
    public static (UIElement Editor, KeyCaptureModel Model) BuildKeyCaptureEditor(EditorDescription desc, InspectorCallbacks callbacks)
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
                e.Handled = true;
                return;
            }
            bool captured = model.CaptureKey(
                name,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Windows));
            e.Handled = true;
            if (captured)
                box.Text = model.Chord;
        };
        box.LostFocus += (_, _) => model.CancelCapture();

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(btn, Dock.Right);
        row.Children.Add(btn);
        row.Children.Add(box);
        return (row, model);
    }
}
