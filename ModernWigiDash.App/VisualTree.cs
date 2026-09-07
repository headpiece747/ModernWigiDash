using System.Windows;
using System.Windows.Media;

namespace ModernWigiDash.App;

/// <summary>
/// The one spelling of the recursive visual-tree search (candidate 8 of the
/// architecture review): before this module the same <see cref="VisualTreeHelper"/>
/// walk was re-spelled verbatim in SettingsDialog, ThemeDialog, and
/// InspectorController (plus test copies). Now every caller routes through
/// these two entry points, and the production types shed their test-only
/// members.
/// </summary>
internal static class VisualTree
{
    /// <summary>Finds all descendants of type <typeparamref name="T"/> under
    /// <paramref name="root"/>, depth-first.</summary>
    public static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    /// <summary>Finds the first descendant of type <typeparamref name="T"/>
    /// under <paramref name="root"/>, or null when none exists.</summary>
    public static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
        => FindDescendants<T>(root).FirstOrDefault();
}
