namespace ModernWigiDash.Tests;

using ModernWigiDash.App.Inspector;
using ModernWigiDash.Widgets.Todo;

[TestClass]
public class TodoTaskEditorModelTests
{
    [TestMethod]
    public void Parse_EmptyOrMalformed_ReturnsNoDrafts()
    {
        Assert.AreEqual(0, TodoTaskEditorModel.Parse(null).Count);
        Assert.AreEqual(0, TodoTaskEditorModel.Parse("").Count);
        Assert.AreEqual(0, TodoTaskEditorModel.Parse("   ").Count);
        Assert.AreEqual(0, TodoTaskEditorModel.Parse("{not valid json").Count);
        Assert.AreEqual(0, TodoTaskEditorModel.Parse("""{"kind":"not_an_array"}""").Count);
    }

    [TestMethod]
    public void Parse_ValidTasks_PopulatesDrafts()
    {
        string json = """
        [
          {"id":"t-1","title":"Feed pet","isDone":true,"category":"Pet"},
          {"id":"t-2","title":"Code widget","isDone":false,"category":null}
        ]
        """;

        var drafts = TodoTaskEditorModel.Parse(json);

        Assert.AreEqual(2, drafts.Count);
        Assert.AreEqual("t-1", drafts[0].Id);
        Assert.AreEqual("Feed pet", drafts[0].Title);
        Assert.IsTrue(drafts[0].IsDone);
        Assert.AreEqual("Pet", drafts[0].Category);

        Assert.AreEqual("t-2", drafts[1].Id);
        Assert.AreEqual("Code widget", drafts[1].Title);
        Assert.IsFalse(drafts[1].IsDone);
        Assert.AreEqual("", drafts[1].Category, "Null category defaults to empty string in draft");
    }

    [TestMethod]
    public void ToItems_MapsValidDrafts()
    {
        var drafts = new List<TodoTaskDraft>
        {
            new() { Id = "id-1", Title = "Write unit tests", IsDone = true, Category = "Code" },
            new() { Id = "id-2", Title = "Review plan", IsDone = false, Category = "Review" },
        };

        var items = TodoTaskEditorModel.ToItems(drafts);

        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("id-1", items[0].Id);
        Assert.AreEqual("Write unit tests", items[0].Title);
        Assert.IsTrue(items[0].IsDone);
        Assert.AreEqual("Code", items[0].Category);

        Assert.AreEqual("id-2", items[1].Id);
        Assert.AreEqual("Review plan", items[1].Title);
        Assert.IsFalse(items[1].IsDone);
        Assert.AreEqual("Review", items[1].Category);
    }

    [TestMethod]
    public void ToItems_DropsDraftsWithEmptyOrWhitespaceTitles()
    {
        var drafts = new List<TodoTaskDraft>
        {
            new() { Id = "id-1", Title = "Valid task", IsDone = false },
            new() { Id = "id-2", Title = "   ", IsDone = false },
            new() { Id = "id-3", Title = "", IsDone = true },
        };

        var items = TodoTaskEditorModel.ToItems(drafts);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("id-1", items[0].Id);
        Assert.AreEqual("Valid task", items[0].Title);
    }

    [TestMethod]
    public void ToItems_DraftsWithEmptyId_GeneratesFreshGuid()
    {
        var drafts = new List<TodoTaskDraft>
        {
            new() { Id = "", Title = "New Task", IsDone = false },
            new() { Id = "   ", Title = "Another Task", IsDone = false },
        };

        var items = TodoTaskEditorModel.ToItems(drafts);

        Assert.AreEqual(2, items.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(items[0].Id));
        Assert.IsFalse(string.IsNullOrWhiteSpace(items[1].Id));
        Assert.AreNotEqual(items[0].Id, items[1].Id);
    }

    [TestMethod]
    public void ToItems_TrimsTitleAndCategory()
    {
        var drafts = new List<TodoTaskDraft>
        {
            new() { Id = "t-1", Title = "  Padded Title  ", IsDone = false, Category = "  Tag  " },
        };

        var items = TodoTaskEditorModel.ToItems(drafts);

        Assert.AreEqual("Padded Title", items[0].Title);
        Assert.AreEqual("Tag", items[0].Category);
    }

    [TestMethod]
    public void ToItems_AllIncomplete_YieldsEmpty()
    {
        var drafts = new List<TodoTaskDraft>
        {
            new() { Title = "   " }
        };

        Assert.AreEqual(0, TodoTaskEditorModel.ToItems(drafts).Count);
    }

    [TestMethod]
    public void ParseThenToItems_UnEditedFields_SurviveTheRoundTrip()
    {
        string json = """
        [
          {"id":"t-1","title":"Estimate task","isDone":false,"category":"Focus","dueDate":"2026-11-01T09:00:00Z","estimatedMinutes":45}
        ]
        """;

        var source = TodoItemCodec.Parse(json);
        var drafts = TodoTaskEditorModel.Parse(json);

        Assert.AreEqual(1, drafts.Count);
        Assert.AreEqual(source[0].EstimatedMinutes, drafts[0].EstimatedMinutes, "the draft carries the estimate through the parse");
        Assert.AreEqual(source[0].DueDate, drafts[0].DueDate, "the draft carries the due date through the parse");

        // An edit that only touches the title, then the commit: the
        // un-edited fields must survive, not reset to defaults.
        drafts[0].Title = "Estimate task (edited)";
        var items = TodoTaskEditorModel.ToItems(drafts);

        Assert.AreEqual("Estimate task (edited)", items[0].Title);
        Assert.AreEqual(source[0].EstimatedMinutes, items[0].EstimatedMinutes, "an un-edited estimate must survive the commit");
        Assert.AreEqual(source[0].DueDate, items[0].DueDate, "an un-edited due date must survive the commit");
    }
}

[TestClass]
public class TodoTaskEditorRowsTests
{
    private static (TodoTaskEditorRows Rows, List<string> Commits) Make(string? tasksJson = null)
    {
        var commits = new List<string>();
        var rows = new TodoTaskEditorRows(tasksJson, c => commits.Add(c));
        return (rows, commits);
    }

    [TestMethod]
    public void Add_AppendsDraftAndCommits()
    {
        var (rows, commits) = Make();

        rows.Add();

        Assert.AreEqual(1, rows.Drafts.Count);
        Assert.AreEqual("New Task", rows.Drafts[0].Title);
        Assert.AreEqual(1, commits.Count);
        Assert.IsTrue(TodoItemCodec.Parse(commits[0]).Any(i => i.Title == "New Task"));
    }

    [TestMethod]
    public void Remove_DropsRowAndCommits()
    {
        string json = """[{"id":"t-1","title":"Task 1"},{"id":"t-2","title":"Task 2"}]""";
        var (rows, commits) = Make(json);

        rows.Remove(0);

        Assert.AreEqual(1, rows.Drafts.Count);
        Assert.AreEqual("Task 2", rows.Drafts[0].Title);
        Assert.AreEqual(1, commits.Count);
        Assert.IsFalse(TodoItemCodec.Parse(commits[0]).Any(i => i.Title == "Task 1"));
    }

    [TestMethod]
    public void SetDone_TogglesAndCommits()
    {
        string json = """[{"id":"t-1","title":"Task 1","isDone":false}]""";
        var (rows, commits) = Make(json);

        rows.SetDone(0, true);

        Assert.IsTrue(rows.Drafts[0].IsDone);
        Assert.AreEqual(1, commits.Count);
        Assert.IsTrue(TodoItemCodec.Parse(commits[0])[0].IsDone);
    }

    [TestMethod]
    public void SetTitle_UpdatesAndCommits()
    {
        string json = """[{"id":"t-1","title":"Old Title"}]""";
        var (rows, commits) = Make(json);

        rows.SetTitle(0, "Updated Title");

        Assert.AreEqual("Updated Title", rows.Drafts[0].Title);
        Assert.AreEqual(1, commits.Count);
        Assert.AreEqual("Updated Title", TodoItemCodec.Parse(commits[0])[0].Title);
    }

    [TestMethod]
    public void SetCategory_UpdatesAndCommits()
    {
        string json = """[{"id":"t-1","title":"Task 1","category":""}]""";
        var (rows, commits) = Make(json);

        rows.SetCategory(0, "Work");

        Assert.AreEqual("Work", rows.Drafts[0].Category);
        Assert.AreEqual(1, commits.Count);
        Assert.AreEqual("Work", TodoItemCodec.Parse(commits[0])[0].Category);
    }

    [TestMethod]
    public void MoveUp_SwapsWithPreviousAndCommits()
    {
        string json = """[{"id":"t-1","title":"First"},{"id":"t-2","title":"Second"}]""";
        var (rows, commits) = Make(json);

        rows.MoveUp(1);

        Assert.AreEqual("Second", rows.Drafts[0].Title);
        Assert.AreEqual("First", rows.Drafts[1].Title);
        Assert.AreEqual(1, commits.Count);
    }

    [TestMethod]
    public void MoveUp_AtTop_DoesNothing()
    {
        string json = """[{"id":"t-1","title":"First"},{"id":"t-2","title":"Second"}]""";
        var (rows, commits) = Make(json);

        rows.MoveUp(0);

        Assert.AreEqual("First", rows.Drafts[0].Title);
        Assert.AreEqual(0, commits.Count);
    }

    [TestMethod]
    public void MoveDown_SwapsWithNextAndCommits()
    {
        string json = """[{"id":"t-1","title":"First"},{"id":"t-2","title":"Second"}]""";
        var (rows, commits) = Make(json);

        rows.MoveDown(0);

        Assert.AreEqual("Second", rows.Drafts[0].Title);
        Assert.AreEqual("First", rows.Drafts[1].Title);
        Assert.AreEqual(1, commits.Count);
    }

    [TestMethod]
    public void MoveDown_AtBottom_DoesNothing()
    {
        string json = """[{"id":"t-1","title":"First"},{"id":"t-2","title":"Second"}]""";
        var (rows, commits) = Make(json);

        rows.MoveDown(1);

        Assert.AreEqual("Second", rows.Drafts[1].Title);
        Assert.AreEqual(0, commits.Count);
    }

    [TestMethod]
    public void OutOfRangeOperations_DoNotThrowOrCommit()
    {
        var (rows, commits) = Make();

        rows.Remove(10);
        rows.Remove(-1);
        rows.MoveUp(0);
        rows.MoveUp(-1);
        rows.MoveDown(0);
        rows.MoveDown(5);
        rows.SetDone(5, true);
        rows.SetTitle(5, "Test");
        rows.SetCategory(5, "Category");

        Assert.AreEqual(0, rows.Drafts.Count);
        Assert.AreEqual(0, commits.Count);
    }
}
