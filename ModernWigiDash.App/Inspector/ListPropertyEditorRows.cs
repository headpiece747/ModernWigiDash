namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Deep base class for list property editors in the inspector.
/// Encapsulates draft list storage, bounds checking, reordering (MoveUp/MoveDown),
/// item removal, per-draft mutation, and serialization dispatch through the
/// write-back funnel. Subclasses express intent only: every mutation routes
/// through an intent method that bounds-checks and commits, so the raw draft
/// list is never exposed (no escape hatch).
/// </summary>
/// <typeparam name="TDraft">The editable draft item type.</typeparam>
internal abstract class ListPropertyEditorRows<TDraft>
{
    private readonly List<TDraft> _drafts;
    private readonly Action<string> _commit;

    protected ListPropertyEditorRows(IEnumerable<TDraft> initialDrafts, Action<string> commit)
    {
        _drafts = initialDrafts.ToList();
        _commit = commit;
    }

    /// <summary>The current drafts in display order.</summary>
    public IReadOnlyList<TDraft> Drafts => _drafts;

    /// <summary>
    /// The draft at <paramref name="index"/>, or null when the index is out
    /// of range. The one owner of the bounds guard: every intent method
    /// routes through it, so a subclass transition that reads a row (the
    /// credential seam) cannot drift from the guard by re-checking bounds on
    /// its own.
    /// </summary>
    public TDraft? DraftAt(int index)
        => index >= 0 && index < _drafts.Count ? _drafts[index] : default;

    /// <summary>
    /// Mutates one draft by index and commits (the intent method for per-draft
    /// field edits: the subclass names the field, the base owns the bounds
    /// check and the commit, so a missed commit is unrepresentable).
    /// </summary>
    public void Mutate(int index, Action<TDraft> mutate)
    {
        if (DraftAt(index) is { } draft)
        {
            mutate(draft);
            Commit();
        }
    }

    /// <summary>Appends a draft and commits.</summary>
    public void Add(TDraft draft)
    {
        _drafts.Add(draft);
        Commit();
    }

    /// <summary>Removes a draft by index and commits.</summary>
    public void Remove(int index)
    {
        if (DraftAt(index) is not null)
        {
            _drafts.RemoveAt(index);
            Commit();
        }
    }

    /// <summary>Moves a draft up one position and commits.</summary>
    public void MoveUp(int index)
    {
        if (index <= 0 || index >= _drafts.Count) return;
        (_drafts[index - 1], _drafts[index]) = (_drafts[index], _drafts[index - 1]);
        Commit();
    }

    /// <summary>Moves a draft down one position and commits.</summary>
    public void MoveDown(int index)
    {
        if (index < 0 || index >= _drafts.Count - 1) return;
        (_drafts[index], _drafts[index + 1]) = (_drafts[index + 1], _drafts[index]);
        Commit();
    }

    /// <summary>Invokes serialization and forwards the result through the commit funnel.</summary>
    public void Commit() => _commit(Serialize(_drafts));

    /// <summary>Maps the drafts into the persisted string payload.</summary>
    protected abstract string Serialize(IReadOnlyList<TDraft> drafts);
}
