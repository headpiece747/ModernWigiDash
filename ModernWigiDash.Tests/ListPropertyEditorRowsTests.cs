namespace ModernWigiDash.Tests;

using ModernWigiDash.App.Inspector;

/// <summary>
/// The list-property editor base at its own interface: the draft list
/// storage, the bounds guards, the reorder/remove/mutate transitions, and
/// the commit funnel. Driven through a minimal concrete subclass so the
/// base's transitions are assertable without a live inspector row (the
/// subclass expresses intent only; every mutation routes through the
/// base's bounds-checked intent methods). The draft is a mutable object
/// (the house pattern: editable draft, serialized string payload) because
/// the mutate seam writes the draft in place.
/// </summary>
[TestClass]
public class ListPropertyEditorRowsTests
{
    /// <summary>The editable draft item (the house draft shape: a mutable field, a serialized form).</summary>
    private sealed class Row
    {
        public string Text { get; set; } = "";
    }

    private sealed class TestRows(IEnumerable<Row> initial, List<string> commits) : ListPropertyEditorRows<Row>(initial, commits.Add)
    {
        protected override string Serialize(IReadOnlyList<Row> drafts) => string.Join(",", drafts.Select(d => d.Text));
    }

    private static (TestRows Rows, List<string> Commits) Create(params string[] initial)
    {
        var commits = new List<string>();
        return (new TestRows(initial.Select(t => new Row { Text = t }), commits), commits);
    }

    private static string[] Texts(TestRows rows) => [.. rows.Drafts.Select(d => d.Text)];

    [TestMethod]
    public void DraftAt_OutOfRange_ReturnsDefault()
    {
        var (rows, _) = Create("a", "b");

        Assert.IsNull(rows.DraftAt(-1));
        Assert.IsNull(rows.DraftAt(2));
        Assert.IsNull(rows.DraftAt(99));
    }

    [TestMethod]
    public void DraftAt_Valid_ReturnsTheDraft()
    {
        var (rows, _) = Create("a", "b");

        Assert.AreEqual("a", rows.DraftAt(0)!.Text);
        Assert.AreEqual("b", rows.DraftAt(1)!.Text);
    }

    [TestMethod]
    public void Add_AppendsAndCommitsTheSerializedPayload()
    {
        var (rows, commits) = Create("a", "b");

        rows.Add(new Row { Text = "c" });

        Assert.AreSequenceEqual(new[] { "a", "b", "c" }, Texts(rows));
        Assert.AreSequenceEqual(new[] { "a,b,c" }, commits);
    }

    [TestMethod]
    public void Remove_OutOfRange_DoesNotCommit()
    {
        var (rows, commits) = Create("a", "b");

        rows.Remove(99);

        Assert.AreEqual(2, rows.Drafts.Count);
        Assert.AreEqual(0, commits.Count);
    }

    [TestMethod]
    public void Remove_Valid_RemovesAndCommits()
    {
        var (rows, commits) = Create("a", "b", "c");

        rows.Remove(1);

        Assert.AreSequenceEqual(new[] { "a", "c" }, Texts(rows));
        Assert.AreSequenceEqual(new[] { "a,c" }, commits);
    }

    [TestMethod]
    public void MoveUp_FirstIndex_IsANoOpWithoutACommit()
    {
        var (rows, commits) = Create("a", "b");

        rows.MoveUp(0);

        Assert.AreSequenceEqual(new[] { "a", "b" }, Texts(rows));
        Assert.AreEqual(0, commits.Count);
    }

    [TestMethod]
    public void MoveDown_LastIndex_IsANoOpWithoutACommit()
    {
        var (rows, commits) = Create("a", "b");

        rows.MoveDown(1);

        Assert.AreSequenceEqual(new[] { "a", "b" }, Texts(rows));
        Assert.AreEqual(0, commits.Count);
    }

    [TestMethod]
    public void MoveUp_Middle_SwapsAndCommits()
    {
        var (rows, commits) = Create("a", "b", "c");

        rows.MoveUp(2);

        Assert.AreSequenceEqual(new[] { "a", "c", "b" }, Texts(rows));
        Assert.AreSequenceEqual(new[] { "a,c,b" }, commits);
    }

    [TestMethod]
    public void MoveDown_Middle_SwapsAndCommits()
    {
        var (rows, commits) = Create("a", "b", "c");

        rows.MoveDown(0);

        Assert.AreSequenceEqual(new[] { "b", "a", "c" }, Texts(rows));
        Assert.AreSequenceEqual(new[] { "b,a,c" }, commits);
    }

    [TestMethod]
    public void Mutate_OutOfRange_DoesNotMutateOrCommit()
    {
        var (rows, commits) = Create("a");

        rows.Mutate(99, d => d.Text = "changed");

        Assert.AreSequenceEqual(new[] { "a" }, Texts(rows));
        Assert.AreEqual(0, commits.Count);
    }

    [TestMethod]
    public void Mutate_Valid_MutatesAndCommits()
    {
        var (rows, commits) = Create("a", "b");

        rows.Mutate(1, d => d.Text = "B");

        Assert.AreSequenceEqual(new[] { "a", "B" }, Texts(rows));
        Assert.AreSequenceEqual(new[] { "a,B" }, commits);
    }

    [TestMethod]
    public void Commit_ForwardsTheSerializedDraftsThroughTheFunnel()
    {
        var (rows, commits) = Create("a", "b");

        rows.Commit();

        Assert.AreSequenceEqual(new[] { "a,b" }, commits);
    }
}
