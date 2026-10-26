namespace ModernWigiDash.Tests;

using System.Text.Json;
using ModernWigiDash.Widgets.Todo;

[TestClass]
public class SuperProductivityTaskMapperTests
{
    [TestMethod]
    public void Map_BareArray_MapsEveryField()
    {
        string json = """
        [
          {"id":"task-1","title":"Review PR","isDone":false,"timeEstimate":1800000,"tagIds":["work"]},
          {"id":"task-2","title":"Ship it","isDone":true,"timeEstimate":60000}
        ]
        """;

        var items = SuperProductivityTaskMapper.Map(json);

        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("task-1", items[0].Id);
        Assert.AreEqual("Review PR", items[0].Title);
        Assert.IsFalse(items[0].IsDone);
        Assert.AreEqual("work", items[0].Category);
        Assert.AreEqual(30, items[0].EstimatedMinutes);
        Assert.AreEqual("task-2", items[1].Id);
        Assert.IsTrue(items[1].IsDone);
        Assert.AreEqual(1, items[1].EstimatedMinutes);
    }

    [TestMethod]
    public void Map_DataEnvelope_MapsTheInnerArray()
    {
        string json = """
        {"data":[{"id":"task-9","title":"Wrapped","isDone":false}]}
        """;

        var items = SuperProductivityTaskMapper.Map(json);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("task-9", items[0].Id);
        Assert.AreEqual("Wrapped", items[0].Title);
    }

    [TestMethod]
    public void Map_MissingFields_TakesTheDefaults()
    {
        var items = SuperProductivityTaskMapper.Map("""[{}]""");

        Assert.AreEqual(1, items.Count);
        var item = items[0];
        Assert.AreEqual(32, item.Id.Length, "a missing id takes a generated 32-char GUID");
        Assert.AreEqual("Task", item.Title);
        Assert.IsFalse(item.IsDone);
        Assert.IsNull(item.Category);
        Assert.AreEqual(0, item.EstimatedMinutes);
    }

    [TestMethod]
    public void Map_EmptyArray_YieldsAnEmptyList()
    {
        Assert.AreEqual(0, SuperProductivityTaskMapper.Map("[]").Count);
    }

    [TestMethod]
    public void Map_ShapelessBody_MapsToAnEmptyList()
    {
        // A body that parses but carries no array (a shape the wire has been
        // seen to return on a partial state) is a legitimate empty list, not
        // a fault.
        Assert.AreEqual(0, SuperProductivityTaskMapper.Map("{}").Count);
        Assert.AreEqual(0, SuperProductivityTaskMapper.Map("null").Count);
    }

    [TestMethod]
    public void Map_UnparseableBody_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => SuperProductivityTaskMapper.Map("not json"));
    }

    [TestMethod]
    public void Map_MixedTypesOnTimeEstimate_FallsBackToZero()
    {
        // A timeEstimate that is not a number (a client-version drift) must
        // not sink the whole list.
        string json = """
        [{"id":"task-1","title":"Odd estimate","timeEstimate":"soon"}]
        """;

        var items = SuperProductivityTaskMapper.Map(json);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(0, items[0].EstimatedMinutes);
    }
}
