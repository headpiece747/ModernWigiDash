using System;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;

namespace ModernWigiDash.App;

/// <summary>
/// Atomic profile mutation session: encapsulates all operations that modify the
/// active profile layout (widget additions, removals, clear page, page switching,
/// creation, deletion, renaming, snap-to-grid, background color, and raw writes),
/// guaranteeing that every mutation automatically and atomically executes its
/// post-mutation refresh bundle through <see cref="ProfileMutationFunnel"/>.
/// </summary>
internal sealed class ProfileSession
{
    private readonly Func<ProfileLayout> _profileProvider;
    private readonly IProfileMutationFunnel _funnel;
    private readonly PageManagement _pageManagement;
    private readonly WidgetPluginLoader _loader;
    private readonly IModernWigiDashContext _context;
    private readonly Func<PlacedWidgetInstance?> _selectionProvider;

    public ProfileSession(
        Func<ProfileLayout> profileProvider,
        IProfileMutationFunnel funnel,
        PageManagement pageManagement,
        WidgetPluginLoader loader,
        IModernWigiDashContext context,
        Func<PlacedWidgetInstance?> selectionProvider)
    {
        _profileProvider = profileProvider;
        _funnel = funnel;
        _pageManagement = pageManagement;
        _loader = loader;
        _context = context;
        _selectionProvider = selectionProvider;
    }

    /// <summary>The live profile layout.</summary>
    public ProfileLayout Profile => _profileProvider();

    /// <summary>The currently active page layout.</summary>
    public PageLayout ActivePage => Profile.ActivePage;

    /// <summary>Whether the active page can be deleted.</summary>
    public bool CanDeletePage => _pageManagement.CanDelete();

    /// <summary>
    /// Places a widget centered on the active page, rehydrating it through the loader and context,
    /// selects the newly placed widget, and commits a Transform mutation.
    /// </summary>
    public PlacedWidgetInstance? PlaceWidgetCentered(string pluginId)
    {
        var placed = ProfileOps.PlaceCentered(Profile, _loader, _context, pluginId);
        if (placed is not null)
        {
            _funnel.Apply(ProfileMutationShape.Transform, Profile, placed);
        }
        return placed;
    }

    /// <summary>
    /// Removes the specified widget from the active page, deselects it if it was selected,
    /// and commits a Transform mutation.
    /// </summary>
    public bool RemoveWidget(PlacedWidgetInstance widget)
    {
        if (ProfileOps.RemoveWidget(ActivePage, widget))
        {
            var nextSelection = ReferenceEquals(_selectionProvider(), widget) ? null : _selectionProvider();
            _funnel.Apply(ProfileMutationShape.Transform, Profile, nextSelection);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears all widgets from the active page, clears selection, and commits a Transform mutation.
    /// </summary>
    public bool ClearActivePage()
    {
        if (ActivePage.Widgets.Count == 0) return false;
        ProfileOps.ClearPage(ActivePage);
        _funnel.Apply(ProfileMutationShape.Transform, Profile, null);
        return true;
    }

    /// <summary>
    /// Adds a new page, activates it, and commits a Structural mutation.
    /// </summary>
    public PageOpVerdict AddPage()
    {
        var verdict = _pageManagement.Add();
        if (verdict is PageOpVerdict.Applied)
        {
            _funnel.Apply(ProfileMutationShape.Structural, Profile, null);
        }
        return verdict;
    }

    /// <summary>
    /// Deletes the page at <paramref name="index"/> subject to confirmation and last-page rules,
    /// and commits a Structural mutation upon deletion.
    /// </summary>
    public PageOpVerdict DeletePage(int index)
    {
        var verdict = _pageManagement.Delete(index);
        if (verdict is PageOpVerdict.Applied)
        {
            _funnel.Apply(ProfileMutationShape.Structural, Profile, null);
        }
        return verdict;
    }

    /// <summary>
    /// Renames the page at <paramref name="index"/> subject to dialog prompt,
    /// and commits a Structural mutation upon rename.
    /// </summary>
    public PageOpVerdict RenamePage(int index)
    {
        var verdict = _pageManagement.Rename(index);
        if (verdict is PageOpVerdict.Applied)
        {
            _funnel.Apply(ProfileMutationShape.Structural, Profile, _selectionProvider());
        }
        return verdict;
    }

    /// <summary>
    /// Switches the active page to <paramref name="index"/>, clearing selection
    /// and committing a Structural mutation.
    /// </summary>
    public PageOpVerdict SwitchToPage(int index)
    {
        var verdict = _pageManagement.Switch(index);
        if (verdict is PageOpVerdict.Applied)
        {
            _funnel.Apply(ProfileMutationShape.Structural, Profile, null);
        }
        return verdict;
    }

    /// <summary>
    /// Toggles the snap-to-grid option on the active page and commits a Transform mutation.
    /// </summary>
    public void SetSnapToGrid(bool enabled)
    {
        ActivePage.SnapToGrid = enabled;
        _funnel.Apply(ProfileMutationShape.Transform, Profile, _selectionProvider());
    }

    /// <summary>
    /// Sets the active page's background hex color and commits a Transform mutation.
    /// </summary>
    public void SetPageBackground(string hex)
    {
        ActivePage.BackgroundHexColor = hex;
        _funnel.Apply(ProfileMutationShape.Transform, Profile, _selectionProvider());
    }

    /// <summary>
    /// Wholesale profile replacement (import / raw write).
    /// </summary>
    public void ApplyRawWrite()
    {
        _funnel.Apply(ProfileMutationShape.RawWrite, Profile, null);
    }

    /// <summary>
    /// Notifies that widget geometry or properties have been modified in-place
    /// (e.g. inspector write-back, canvas drag/resize manipulation end).
    /// </summary>
    public void CommitTransform(PlacedWidgetInstance? selection = null)
    {
        _funnel.Apply(ProfileMutationShape.Transform, Profile, selection ?? _selectionProvider());
    }
}
