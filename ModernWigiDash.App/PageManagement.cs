namespace ModernWigiDash.App;

/// <summary>
/// The verdict of a Page-operation attempt (the App's page-management
/// module): <see cref="Applied"/> when the mutation landed (the window runs
/// its structural refresh), or one of the named refusals. A refusal is
/// unrepresentable as "applied", so the window cannot accidentally refresh
/// after a no-op.
/// </summary>
internal enum PageOpVerdict
{
    /// <summary>The page operation landed; the caller should refresh.</summary>
    Applied,

    /// <summary>The last page can never be deleted (the module's rule).</summary>
    LastPage,

    /// <summary>The index was stale (out of range); a silent no-op.</summary>
    StaleIndex,

    /// <summary>The user cancelled the confirm dialog.</summary>
    Cancelled,

    /// <summary>The rename prompt returned a blank name.</summary>
    BlankName
}

/// <summary>
/// The page-management module's dialog seam: the two prompts the page
/// operations need (the delete confirm and the rename text prompt). A
/// <see cref="DialogHost"/> in production, an in-memory fake in tests, so the
/// gate rules are assertable without a window.
/// </summary>
internal interface IPageDialogs
{
    /// <summary>The delete-page confirm; true when the user confirms.</summary>
    bool Confirm(string title, string message);

    /// <summary>The rename text prompt; the entered name, or null/blank when
    /// cancelled.</summary>
    string? PromptForText(string title, string label, string initialValue);
}

/// <summary>
/// The page-management module (App): concentrates the Page add/delete/rename/
/// switch surface (their gate predicates + the ProfileOps mutation) in one
/// place that returns a <see cref="PageOpVerdict"/>. The window keeps only the
/// confirm dialogs' presentation and the single <c>ApplyProfileMutation</c>
/// funnel call; the tab strip consults <see cref="CanDelete"/> for button
/// enablement, so "what a Page mutation may do and what happens after" has
/// one owner instead of being split across four window methods + the strip.
/// The dialog seam is injected (a <see cref="DialogHost"/> in production, an
/// in-memory fake in tests), so the gate rules are assertable without a
/// window.
/// </summary>
internal sealed class PageManagement(Func<ProfileLayout> profileProvider, IPageDialogs dialogs)
{
    private readonly Func<ProfileLayout> _profileProvider = profileProvider;
    private readonly IPageDialogs _dialogs = dialogs;

    /// <summary>Whether the current profile's page can be deleted (the same
    /// predicate the tab strip's button-enablement consults).</summary>
    public bool CanDelete() => ProfileOps.CanDeletePage(_profileProvider());

    /// <summary>Adds a page and activates it. Always applies (no gate).</summary>
    public PageOpVerdict Add()
    {
        ProfileOps.AddPage(_profileProvider());
        return PageOpVerdict.Applied;
    }

    /// <summary>
    /// Deletes the page at <paramref name="index"/>: the last-page veto, a
    /// bounds-safe read of the confirm facts (a stale index is a silent no-op,
    /// never a throw), the confirm when the page holds widgets, then the
    /// delete. Returns the verdict; the window refreshes only on
    /// <see cref="PageOpVerdict.Applied"/>.
    /// </summary>
    public PageOpVerdict Delete(int index)
    {
        var profile = _profileProvider();
        if (!ProfileOps.CanDeletePage(profile)) return PageOpVerdict.LastPage;
        if (ProfileOps.TryGetPage(profile, index) is not { } targetPage) return PageOpVerdict.StaleIndex;
        if (targetPage.Widgets.Count > 0 && !_dialogs.Confirm(
                "Delete Page",
                $"Are you sure you want to delete '{targetPage.PageName}' containing {targetPage.Widgets.Count} widget(s)?"))
            return PageOpVerdict.Cancelled;
        if (!ProfileOps.DeletePage(profile, index)) return PageOpVerdict.StaleIndex;
        return PageOpVerdict.Applied;
    }

    /// <summary>
    /// Renames the page at <paramref name="index"/>: a bounds-safe read, the
    /// rename prompt (a blank answer is a no-op), then the rename. Returns the
    /// verdict; the window refreshes only on <see cref="PageOpVerdict.Applied"/>.
    /// </summary>
    public PageOpVerdict Rename(int index)
    {
        var profile = _profileProvider();
        if (ProfileOps.TryGetPage(profile, index) is not { } page) return PageOpVerdict.StaleIndex;
        string? newName = _dialogs.PromptForText("Rename Page", $"New name for '{page.PageName}':", page.PageName);
        if (string.IsNullOrWhiteSpace(newName)) return PageOpVerdict.BlankName;
        ProfileOps.RenamePage(page, newName);
        return PageOpVerdict.Applied;
    }

    /// <summary>
    /// Switches the active page through the SetActivePageIndex gate (an
    /// out-of-range step is a no-op, never a wrap). Returns the verdict; the
    /// window refreshes only on <see cref="PageOpVerdict.Applied"/>.
    /// </summary>
    public PageOpVerdict Switch(int index)
    {
        if (!ProfileOps.SetActivePageIndex(_profileProvider(), index)) return PageOpVerdict.StaleIndex;
        return PageOpVerdict.Applied;
    }

    /// <summary>The production dialog-seam adapter: routes the module's two
    /// prompts to the host's <see cref="DialogHost"/>.</summary>
    internal sealed class DialogHostAdapter(DialogHost host) : IPageDialogs
    {
        public bool Confirm(string title, string message) => host.Confirm(title, message);
        public string? PromptForText(string title, string label, string initialValue)
            => host.PromptForText(title, label, initialValue);
    }
}
