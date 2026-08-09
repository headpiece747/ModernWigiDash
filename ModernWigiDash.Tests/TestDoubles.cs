using System.Net;
using System.Net.Http;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared test doubles every test file used to copy: the no-op widget
/// context (with optional counters), the placed-instance persisting variant,
/// the in-memory WebSocket feed, the HTTP stub, and the PresentMon interop
/// stub. One double per seam — new widget tests start from a one-line host.
/// </summary>
internal class TestContext : IModernWigiDashContext
{
    public int Renders { get; private set; }
    public int AuthShown { get; private set; }
    public int AuthClosed { get; private set; }
    public List<string> Errors { get; } = [];
    public List<string> Infos { get; } = [];

    public void LogInfo(string message) => Infos.Add(message);
    public void LogError(string message, Exception? ex = null) => Errors.Add(message);
    public void RequestRender() => Renders++;
    public void RequestInspectorRefresh() { }
    public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) => AuthShown++;
    public void CloseDeviceAuthorization() => AuthClosed++;

    public virtual void PersistProperty(object widget, string propertyName, object? value) { }
}

/// <summary>
/// Context that resolves the owning placed instance like MainWindow does —
/// the companion to ModernWidgetBase.SetProperty (for tests asserting the
/// PropertyValues persistence path).
/// </summary>
internal sealed class PersistingContext(ProfileLayout profile) : TestContext
{
    public override void PersistProperty(object widget, string propertyName, object? value)
    {
        foreach (var page in profile.Pages)
        {
            foreach (var placed in page.Widgets)
            {
                if (!ReferenceEquals(placed.ActiveInstance, widget)) continue;
                placed.PropertyValues[propertyName] = value;
                return;
            }
        }
    }
}

/// <summary>
/// In-memory <see cref="IWebSocketFeed"/>: queued messages feed the consumer,
/// sent payloads are recorded, and connect failures are injectable — the
/// feed loops (price, Twitch) are drivable without a network.
/// </summary>
internal sealed class FakeFeed : IWebSocketFeed
{
    private readonly Queue<string> _incoming = new();
    public List<string> Sent { get; } = [];
    public bool IsOpen { get; set; } = true;
    public int ConnectCount { get; private set; }
    public Exception? ConnectError { get; set; }

    public void QueueMessage(string message) => _incoming.Enqueue(message);

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectCount++;
        return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
    }

    public Task SendTextAsync(string payload, CancellationToken ct)
    {
        Sent.Add(payload);
        return Task.CompletedTask;
    }

    public Task<string?> ReceiveTextAsync(CancellationToken ct)
        => Task.FromResult(_incoming.Count > 0 ? _incoming.Dequeue() : null);

    public void Abort() => IsOpen = false;
    public void Dispose() { }
}

/// <summary>
/// <see cref="HttpMessageHandler"/> stub: responds per request via the
/// delegate (or a canned body). Use the static factories for the common
/// single-body and not-found shapes.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public int Calls { get; private set; }
    public List<string> RequestUrls { get; } = [];

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public StubHttpHandler(string body)
        : this(_ => Ok(body))
    {
    }

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        RequestUrls.Add(request.RequestUri?.ToString() ?? "");
        return Task.FromResult(_respond(request));
    }
}

/// <summary>PresentMon interop stub — keeps the real PresentMonAPI2.dll (and
/// its load-time side effects) out of the test host.</summary>
internal sealed class StubPresentMonNative : IPresentMonNative
{
    public bool IsAvailable => false;
    public string? UnavailableReason => "stub (test)";
    public bool OpenSession() => false;
    public void CloseSession() { }
    public bool TrackProcess(int processId) => false;
    public PresentMonPollResult PollDynamic(int processId) => new(null, PmStatus.Success);
    public IReadOnlyList<double> DrainFrameTimes(int processId) => [];
    public void Dispose() { }
}
