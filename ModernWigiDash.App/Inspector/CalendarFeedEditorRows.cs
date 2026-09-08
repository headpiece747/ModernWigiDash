using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// The calendar feed editor's row-state: the one owner of the editable draft list
/// and its transitions (add, remove, edit a field, swap kind, save a credential).
/// The inspector renderer is a thin mapper over this module — it renders the
/// current rows and forwards user events to these transitions, exactly like its
/// sibling editors route their decision rules to a separate model. Every mutation
/// commits the whole list as a single <c>FeedsJson</c> value through the write-back
/// funnel (the <see cref="Commit"/> callback); the CalDAV password never rides that
/// commit — it routes through the credential seam instead, so the secret stays
/// machine-local and out of the traveling profile.
/// </summary>
internal sealed class CalendarFeedEditorRows
{
    private readonly List<CalendarFeedDraft> _drafts;
    private readonly Action<string> _commit;
    private readonly Action<string, string>? _saveCredential;

    public CalendarFeedEditorRows(string? feedsJson, Action<string> commit, Action<string, string>? saveCredential)
    {
        _drafts = CalendarFeedEditorModel.Parse(feedsJson).ToList();
        _commit = commit;
        _saveCredential = saveCredential;
    }

    /// <summary>The current drafts in display order (one per feed row).</summary>
    public IReadOnlyList<CalendarFeedDraft> Drafts => _drafts;

    /// <summary>Adds a blank feed row and commits the new list.</summary>
    public void Add()
    {
        _drafts.Add(new CalendarFeedDraft());
        Commit();
    }

    /// <summary>Removes the row at <paramref name="index"/> and commits the new list.</summary>
    public void Remove(int index)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts.RemoveAt(index);
        Commit();
    }

    /// <summary>Sets the row's kind ("ics" or "caldav") and commits.</summary>
    public void SetKind(int index, string kind)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].Kind = kind;
        Commit();
    }

    /// <summary>Sets the row's label and commits.</summary>
    public void SetLabel(int index, string label)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].Label = label;
        Commit();
    }

    /// <summary>Sets the .ics URL for an ics row and commits.</summary>
    public void SetUrl(int index, string url)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].Url = url;
        Commit();
    }

    /// <summary>Sets the CalDAV server base and commits.</summary>
    public void SetServer(int index, string server)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].Server = server;
        Commit();
    }

    /// <summary>Sets the CalDAV principal path and commits.</summary>
    public void SetPrincipalPath(int index, string path)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].PrincipalPath = path;
        Commit();
    }

    /// <summary>Sets the CalDAV username and commits.</summary>
    public void SetUsername(int index, string username)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].Username = username;
        Commit();
    }

    /// <summary>Sets the row's enabled flag and commits.</summary>
    public void SetEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= _drafts.Count) return;
        _drafts[index].Enabled = enabled;
        Commit();
    }

    /// <summary>Saves the machine-local CalDAV password for the row's feed id
    /// through the credential seam (never the persisted JSON). A blank feed id
    /// or a missing credential seam is a no-op (a half-entered row must not
    /// overwrite a good credential with an empty one).</summary>
    public void SavePassword(int index, string password)
    {
        if (index < 0 || index >= _drafts.Count) return;
        var feedId = _drafts[index].FeedId;
        if (_saveCredential is null || string.IsNullOrEmpty(feedId)) return;
        _saveCredential(feedId, password);
    }

    /// <summary>Commits the whole list as a single <c>FeedsJson</c> value through
    /// the write-back funnel (the one spelling of the round-trip, owned by
    /// <see cref="CalendarFeedEditorModel"/>).</summary>
    private void Commit() => _commit(CalendarFeedEditorModel.Serialize(_drafts));
}
