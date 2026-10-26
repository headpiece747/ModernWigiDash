namespace ModernWigiDash.Tests;

using System.IO;
using ModernWigiDash.Widgets.Todo;

[TestClass]
public class TodoStoreTests
{
    [TestMethod]
    public void SeedStarterState_FirstRunStore_WritesTheStarterTasks()
    {
        string statePath = Path.Combine(Path.GetTempPath(), "wmd-todo-store-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new TodoStore(statePath);
        try
        {
            Assert.IsFalse(store.Exists);

            store.SeedStarterState();

            var state = store.Read();
            Assert.AreEqual(3, state.Tasks.Count);
            Assert.IsTrue(state.Tasks[0].IsDone, "the first starter task arrives pre-completed");
        }
        finally
        {
            DeleteStateDir(statePath);
        }
    }

    [TestMethod]
    public void SeedStarterState_ExistingStore_IsANoOp()
    {
        string statePath = Path.Combine(Path.GetTempPath(), "wmd-todo-store-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new TodoStore(statePath);
        try
        {
            store.Write(new TodoDataState { Tasks = [new TodoItem("keep-me", "Existing task", false)] });

            store.SeedStarterState();

            var state = store.Read();
            Assert.AreEqual(1, state.Tasks.Count);
            Assert.AreEqual("keep-me", state.Tasks[0].Id);
        }
        finally
        {
            DeleteStateDir(statePath);
        }
    }

    private static void DeleteStateDir(string statePath)
    {
        string? dir = Path.GetDirectoryName(statePath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, true);
    }
}
