using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ModernWigiDash.App.Controls;

/// <summary>
/// Keeps a Popup inside the window's client area. WPF positions popups against
/// the screen, so a popup near the window's bottom edge extends below the
/// window where it can't be used. Placement prefers below, then above, then a
/// clamped fallback. Extracted from the inspector's combo-dropdown clamp so
/// combo popups and the color picker popup share one placement rule.
/// </summary>
public static class PopupClamp
{
    /// <summary>
    /// Pure placement math: candidate positions relative to the placement
    /// target, in preference order (below → above → clamped). Mirrors the
    /// pre-extraction inspector logic exactly.
    /// </summary>
    public static CustomPopupPlacement[] ComputePlacements(
        Size popupSize, Size targetSize, Point targetTopLeft, Size clientSize)
    {
        List<CustomPopupPlacement> placements = [];
        if (clientSize.Height - (targetTopLeft.Y + targetSize.Height) >= popupSize.Height)
        {
            placements.Add(new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.Horizontal));
        }
        if (targetTopLeft.Y >= popupSize.Height)
        {
            placements.Add(new CustomPopupPlacement(new Point(0, -popupSize.Height), PopupPrimaryAxis.Horizontal));
        }

        double popupLeft = Math.Clamp(targetTopLeft.X, 0, Math.Max(0, clientSize.Width - popupSize.Width));
        double popupTop = Math.Clamp(targetTopLeft.Y + targetSize.Height, 0, Math.Max(0, clientSize.Height - popupSize.Height));
        placements.Add(new CustomPopupPlacement(new Point(popupLeft - targetTopLeft.X, popupTop - targetTopLeft.Y), PopupPrimaryAxis.Horizontal));
        return placements.ToArray();
    }

    /// <summary>
    /// Attaches a Custom-placement clamp to <paramref name="popup"/>: the
    /// callback resolves the target's window client area at placement time.
    /// </summary>
    public static void AttachPopupWithinWindow(Popup popup, FrameworkElement target)
    {
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            if (Window.GetWindow(target) is not Window window) return ComputePlacements(popupSize, targetSize, new Point(0, 0), new Size(0, 0));
            if (window.Content is not FrameworkElement content) return ComputePlacements(popupSize, targetSize, new Point(0, 0), new Size(window.ActualWidth, window.ActualHeight));

            double clientW = content.ActualWidth > 0 ? content.ActualWidth : window.ActualWidth;
            double clientH = content.ActualHeight > 0 ? content.ActualHeight : window.ActualHeight;
            var tl = target.TransformToAncestor(content).Transform(new Point(0, 0));
            return ComputePlacements(popupSize, targetSize, tl, new Size(clientW, clientH));
        };
    }
}
