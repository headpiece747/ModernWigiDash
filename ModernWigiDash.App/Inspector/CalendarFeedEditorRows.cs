namespace ModernWigiDash.App.Inspector;

/// <summary>
/// The calendar feed editor's row-state: the one owner of the editable draft list
/// and its transitions (add, remove, edit a field, swap kind, save a credential).
/// The inspector renderer is a thin mapper over this module — it renders the
/// current rows and forwards user events to these transitions, exactly like its
/// sibling editors route their decision rules to a separate model. Every mutation
/// commits the whole list as a single <c>FeedsJson</c> value through the write-back
/// funnel (the <see cref="ListPropertyEditorRows{TDraft}.Commit"/> callback); the CalDAV password never rides that
/// commit — it routes through the credential seam instead, so the secret stays
/// machine-local and out of the traveling profile.
/// </summary>
internal sealed class CalendarFeedEditorRows : ListPropertyEditorRows<CalendarFeedDraft>
{
    private readonly Action<string, string>? _saveCredential;

    public CalendarFeedEditorRows(string? feedsJson, Action<string> commit, Action<string, string>? saveCredential)
        : base(CalendarFeedEditorModel.Parse(feedsJson), commit)
    {
        _saveCredential = saveCredential;
    }

    /// <summary>Adds a blank feed row and commits the new list.</summary>
    public void Add() => Add(new CalendarFeedDraft());

    /// <summary>Sets the row's kind ("ics" or "caldav") and commits.</summary>
    public void SetKind(int index, string kind) => Mutate(index, draft => draft.Kind = kind);

    /// <summary>Sets the row's label and commits.</summary>
    public void SetLabel(int index, string label) => Mutate(index, draft => draft.Label = label);

    /// <summary>Sets the .ics URL for an ics row and commits.</summary>
    public void SetUrl(int index, string url) => Mutate(index, draft => draft.Url = url);

    /// <summary>Sets the CalDAV server base and commits.</summary>
    public void SetServer(int index, string server) => Mutate(index, draft => draft.Server = server);

    /// <summary>Sets the CalDAV principal path and commits.</summary>
    public void SetPrincipalPath(int index, string path) => Mutate(index, draft => draft.PrincipalPath = path);

    /// <summary>Sets the CalDAV username and commits.</summary>
    public void SetUsername(int index, string username) => Mutate(index, draft => draft.Username = username);

    /// <summary>Sets the row's enabled flag and commits.</summary>
    public void SetEnabled(int index, bool enabled) => Mutate(index, draft => draft.Enabled = enabled);

    /// <summary>Saves the machine-local CalDAV password for the row's feed id
    /// through the credential seam (never the persisted JSON). A blank feed id
    /// or a missing credential seam is a no-op (a half-entered row must not
    /// overwrite a good credential with an empty one).</summary>
    public void SavePassword(int index, string password)
    {
        // The bounds guard is the base's DraftAt (the side door that
        // re-checked bounds on its own is closed): an out-of-range index
        // reads null and no-ops exactly like the intent methods.
        var draft = DraftAt(index);
        if (draft is null || _saveCredential is null || string.IsNullOrEmpty(draft.FeedId)) return;
        _saveCredential(draft.FeedId, password);
    }

    protected override string Serialize(IReadOnlyList<CalendarFeedDraft> drafts)
        => CalendarFeedEditorModel.Serialize(drafts);
}
