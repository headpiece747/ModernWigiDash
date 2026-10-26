using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The Super Productivity wire client: the base-URL + token plumbing and
/// the two requests (the tag query with its /tasks fallback ladder, the
/// PATCH mutation). It returns parsed items and typed verdicts and owns
/// no cache — the cache decision stays with the provider.
/// </summary>
internal sealed class SuperProductivityClient
{
    private readonly string _baseUrl;
    private readonly Func<string?> _tokenResolver;
    private readonly HttpClient _httpClient;

    /// <summary>Initializes the wire client over the injected HTTP client (the test seam).</summary>
    public SuperProductivityClient(string baseUrl, Func<string?> tokenResolver, HttpClient httpClient)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _tokenResolver = tokenResolver;
        _httpClient = httpClient;
    }

    /// <summary>
    /// The task-list read: the tag query, with the /tasks fallback when
    /// the tag query is unsupported. Null is the declined verdict (a
    /// 401/403 on the leg that answered) — the caller keeps its cached
    /// view. An unparseable body throws JsonException.
    /// </summary>
    public async ValueTask<IReadOnlyList<TodoItem>?> GetTasksAsync(CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "/tasks?tagId=TODAY&includeDone=true");
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return null;
            }

            // Fallback to /tasks if the tag query is unsupported
            using var fallbackRequest = CreateRequest(HttpMethod.Get, "/tasks");
            using var fallbackResponse = await _httpClient.SendAsync(fallbackRequest, ct).ConfigureAwait(false);
            if (!fallbackResponse.IsSuccessStatusCode)
            {
                return null;
            }
            return SuperProductivityTaskMapper.Map(await fallbackResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        return SuperProductivityTaskMapper.Map(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The PATCH mutation's typed verdict (the cache decision stays with the caller).</summary>
    public async ValueTask<TaskMutationOutcome> CompleteTaskAsync(string taskId, bool isDone, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Patch, $"/tasks/{taskId}");
            string payload = JsonSerializer.Serialize(new { isDone });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? TaskMutationOutcome.Confirmed : TaskMutationOutcome.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return TaskMutationOutcome.Faulted;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        string? token = _tokenResolver();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("token", token);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }
}
