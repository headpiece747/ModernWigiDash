namespace ModernWigiDash.Tests;

using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Todo;

[TestClass]
public class SuperProductivityTaskProviderTests
{
    private const string SampleTasksJson = """
    [
      {
        "id": "task-1",
        "title": "Review PR for Widget",
        "isDone": false,
        "timeEstimate": 1800000,
        "tagIds": ["Work"]
      },
      {
        "id": "task-2",
        "title": "Daily Standup",
        "isDone": true,
        "timeEstimate": 900000,
        "tagIds": ["Meeting"]
      }
    ]
    """;

    [TestMethod]
    public async Task GetTasksAsync_ValidJsonResponse_ParsesTasksCorrectly()
    {
        var stub = new StubHttpHandler(SampleTasksJson);
        using var client = new HttpClient(stub);
        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            tokenResolver: () => "test-token-123",
            httpClient: client);

        var tasks = await provider.GetTasksAsync();

        Assert.AreEqual(2, tasks.Count);
        Assert.AreEqual("task-1", tasks[0].Id);
        Assert.AreEqual("Review PR for Widget", tasks[0].Title);
        Assert.IsFalse(tasks[0].IsDone);
        Assert.AreEqual("Work", tasks[0].Category);
        Assert.AreEqual(30, tasks[0].EstimatedMinutes);

        Assert.AreEqual("task-2", tasks[1].Id);
        Assert.AreEqual("Daily Standup", tasks[1].Title);
        Assert.IsTrue(tasks[1].IsDone);
        Assert.AreEqual("Meeting", tasks[1].Category);
        Assert.AreEqual(15, tasks[1].EstimatedMinutes);
    }

    [TestMethod]
    public async Task GetTasksAsync_InjectsTokenHeaders()
    {
        string? capturedTokenHeader = null;
        string? capturedAuthHeader = null;

        var stub = new StubHttpHandler(req =>
        {
            if (req.Headers.TryGetValues("token", out var vals))
            {
                capturedTokenHeader = vals.FirstOrDefault();
            }
            capturedAuthHeader = req.Headers.Authorization?.Parameter;
            return StubHttpHandler.Ok("[]");
        });

        using var client = new HttpClient(stub);
        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            tokenResolver: () => "secret-token-xyz",
            httpClient: client);

        _ = await provider.GetTasksAsync();

        Assert.AreEqual("secret-token-xyz", capturedTokenHeader);
        Assert.AreEqual("secret-token-xyz", capturedAuthHeader);
    }

    [TestMethod]
    public async Task GetTasksAsync_WhenServerReturns401_ReturnsCachedTasksWithoutThrowing()
    {
        var stub = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(stub);
        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            tokenResolver: () => "invalid-token",
            httpClient: client);

        var tasks = await provider.GetTasksAsync();

        Assert.IsNotNull(tasks);
        Assert.AreEqual(0, tasks.Count);
    }

    [TestMethod]
    public async Task GetTasksAsync_WhenNetworkThrows_DoesNotCrash()
    {
        var stub = new StubHttpHandler(_ => throw new HttpRequestException("Connection refused (127.0.0.1:3876)"));
        using var client = new HttpClient(stub);
        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            tokenResolver: () => null,
            httpClient: client);

        var tasks = await provider.GetTasksAsync();

        Assert.IsNotNull(tasks);
        Assert.AreEqual(0, tasks.Count);
    }

    [TestMethod]
    public async Task CompleteTaskAsync_SendsPatchRequestWithIsDone()
    {
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;
        string? capturedBody = null;

        var stubTasksJson = """[{"id":"task-99","title":"Do work","isDone":false}]""";

        using var client = new HttpClient(new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Ok(stubTasksJson);
            }

            capturedMethod = req.Method;
            capturedPath = req.RequestUri?.AbsolutePath;
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpHandler.Ok("{}");
        }));

        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        _ = await provider.GetTasksAsync();
        var updated = await provider.CompleteTaskAsync("task-99", true);

        Assert.IsNotNull(updated);
        Assert.IsTrue(updated.IsDone);
        Assert.AreEqual(HttpMethod.Patch, capturedMethod);
        Assert.AreEqual("/tasks/task-99", capturedPath);
        Assert.IsTrue(capturedBody?.Contains("\"isDone\":true") == true);
    }

    [TestMethod]
    public async Task FocusTimer_StartAndStop_TracksFocusState()
    {
        await using var provider = new SuperProductivityTaskProvider();

        var initial = await provider.GetFocusStateAsync();
        Assert.IsFalse(initial.IsRunning);

        await provider.StartFocusTimerAsync("task-focus-1");
        var active = await provider.GetFocusStateAsync();
        Assert.IsTrue(active.IsRunning);
        Assert.AreEqual("task-focus-1", active.ActiveTaskId);

        await provider.StopFocusTimerAsync();
        var stopped = await provider.GetFocusStateAsync();
        Assert.IsFalse(stopped.IsRunning);
    }

    [TestMethod]
    public async Task CompleteTaskAsync_WhenServerReturns401_ReturnsNullAndDoesNotMutateCache()
    {
        var stubTasksJson = """[{"id":"task-99","title":"Do work","isDone":false}]""";

        using var client = new HttpClient(new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Ok(stubTasksJson);
            }
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }));

        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        var tasks = await provider.GetTasksAsync();
        Assert.AreEqual(1, tasks.Count);
        Assert.IsFalse(tasks[0].IsDone);

        var result = await provider.CompleteTaskAsync("task-99", true);

        Assert.IsNull(result, "CompleteTaskAsync must return null when server rejects the mutation");
        var tasksAfter = await provider.GetTasksAsync();
        Assert.IsFalse(tasksAfter[0].IsDone, "Cached task must not be mutated on server error");
    }

    [TestMethod]
    public async Task CompleteTaskAsync_OnNetworkFault_ReturnsNullAndDoesNotMutateCache()
    {
        var stubTasksJson = """[{"id":"task-99","title":"Do work","isDone":false}]""";

        using var client = new HttpClient(new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Ok(stubTasksJson);
            }
            throw new HttpRequestException("connection refused");
        }));

        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        var tasks = await provider.GetTasksAsync();
        Assert.AreEqual(1, tasks.Count);
        Assert.IsFalse(tasks[0].IsDone);

        var before = provider.GetCompanionState();
        var result = await provider.CompleteTaskAsync("task-99", true);

        Assert.IsNull(result, "an unconfirmed mutation must read as a failure, not a success");
        var tasksAfter = await provider.GetTasksAsync();
        Assert.IsFalse(tasksAfter[0].IsDone, "the cache must stay untouched on a network fault (the next poll is the source of truth)");
        Assert.AreEqual(before.TotalXp, provider.GetCompanionState().TotalXp, "no companion reward may fire for a completion the server never confirmed");
    }

    [TestMethod]
    public async Task CompleteTaskAsync_OnServerSuccess_ReactsTheCompanion()
    {
        var stubTasksJson = """[{"id":"task-99","title":"Do work","isDone":false}]""";

        using var client = new HttpClient(new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Ok(stubTasksJson);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await using var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        var before = provider.GetCompanionState();
        _ = await provider.GetTasksAsync(); // prime the cache (the provider does not fetch in its constructor)
        var result = await provider.CompleteTaskAsync("task-99", true);

        Assert.IsNotNull(result);
        var after = provider.GetCompanionState();
        Assert.AreEqual(before.TotalXp + 10, after.TotalXp, "a server-confirmed completion must grant the task-completion XP");
        Assert.AreEqual(before.SnackTreats + 1, after.SnackTreats);
        Assert.IsNotNull(after.LastTaskCompletedUtc, "the completion stamp must move so the companion's mood can go Happy");
    }

    [TestMethod]
    public void SetTasks_ReplacesTheCachedView()
    {
        using var client = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Ok("""[]""")));
        var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        provider.SetTasks([new TodoItem("t-1", "Committed", false)]);

        var snapshot = provider.GetSnapshot(DateTime.UtcNow);
        Assert.AreEqual(1, snapshot.Tasks.Count);
        Assert.AreEqual("t-1", snapshot.Tasks[0].Id, "the commit must replace the cached view (the server reconciles it on the next poll)");
    }

    [TestMethod]
    public void SetCompanionName_PersistsTheName()
    {
        using var client = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Ok("""[]""")));
        var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        provider.SetCompanionName("  Mochi ");

        Assert.AreEqual("Mochi", provider.GetCompanionState().Name, "the companion name is persisted local gamification state in Super Productivity mode too");
    }

    [TestMethod]
    public async Task CommitTasks_SuperProductivityMode_IsRepresentableNotANoop()
    {
        using var client = new HttpClient(new StubHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Ok("""[{"id":"t-9","title":"From inspector","isDone":false}]""");
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var provider = new SuperProductivityTaskProvider(
            baseUrl: "http://127.0.0.1:3876",
            httpClient: client);

        var widget = new CozyTodoWidget
        {
            Provider = "Super Productivity",
            ProviderFactory = _ => provider,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);

        widget.CommitTasks(TodoItemCodec.Serialize([new TodoItem("t-9", "From inspector", false)]));
        await widget.RefreshForTest();

        var snapshot = provider.GetSnapshot(DateTime.UtcNow);
        Assert.AreEqual(1, snapshot.Tasks.Count);
        Assert.AreEqual("t-9", snapshot.Tasks[0].Id, "the commit must reach the provider in Super Productivity mode (the poll reconciles the view against the server), never a silent no-op");
        await widget.DisposeAsync();
    }
}
