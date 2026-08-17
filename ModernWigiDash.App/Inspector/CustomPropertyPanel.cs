using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// The panel face of the inspector: the empty/active visibility shells, the
/// name label, and the custom-properties host — including the focus
/// capture/restore bookkeeping around a rebuild, so a refresh never ejects
/// the user from the field they are editing (the weather widget's inspector
/// refresh fires while Location is being typed). One narrow adapter per
/// face — the window's XAML internals are no longer the controller's state.
/// </summary>
internal sealed class CustomPropertyPanel(
    UIElement emptyPanel,
    UIElement activePanel,
    TextBlock nameText,
    StackPanel customProperties,
    Func<string, object?> tryFindResource)
{
    public UIElement EmptyPanel { get; } = emptyPanel;
    public UIElement ActivePanel { get; } = activePanel;
    public TextBlock NameText { get; } = nameText;
    public StackPanel CustomProperties { get; } = customProperties;
    public Func<string, object?> TryFindResource { get; } = tryFindResource;

    /// <summary>Shows the no-selection state (the empty panel over the active one).</summary>
    public void ShowEmptyState()
    {
        EmptyPanel.Visibility = Visibility.Visible;
        ActivePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the selected-widget state with its display name.</summary>
    public void ShowWidget(string displayName)
    {
        EmptyPanel.Visibility = Visibility.Collapsed;
        ActivePanel.Visibility = Visibility.Visible;
        NameText.Text = displayName;
    }

    /// <summary>
    /// Index of the custom-properties row containing the focused element (or
    /// -1 when focus is elsewhere — transforms, catalog, outside the panel),
    /// plus the focused TextBox's caret offset so it can be restored after
    /// the rebuild.
    /// </summary>
    public (int RowIndex, int CaretIndex) CaptureFocusState()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return (-1, 0);
        var current = focused;
        while (current is not null)
        {
            if (current is UIElement element)
            {
                int idx = CustomProperties.Children.IndexOf(element);
                if (idx >= 0)
                {
                    int caret = focused is TextBox box ? box.CaretIndex : 0;
                    return (idx, caret);
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return (-1, 0);
    }

    /// <summary>
    /// Refocuses the editor in the given rebuilt row (the one the user was
    /// typing in before the refresh), restoring the caret to where it was.
    /// A row's editor is its first focusable child (TextBox or ComboBox).
    /// </summary>
    public void RestoreFocusToRow(int rowIndex, int caretIndex)
    {
        if (rowIndex < 0 || rowIndex >= CustomProperties.Children.Count) return;
        if (CustomProperties.Children[rowIndex] is not DependencyObject row) return;
        var editor = FindFirstFocusable(row);
        if (editor is not TextBox box) { editor?.Focus(); return; }
        box.Focus();
        // Focus lands the caret at 0 on a freshly built TextBox; restore it to
        // where the user was typing (clamped to the current text length).
        box.CaretIndex = Math.Clamp(caretIndex, 0, box.Text.Length);
    }

    private static IInputElement? FindFirstFocusable(DependencyObject root)
    {
        if (root is TextBox or ComboBox) return root as IInputElement;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindFirstFocusable(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
