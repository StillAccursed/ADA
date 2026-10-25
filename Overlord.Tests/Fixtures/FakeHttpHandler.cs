using System.Net;
using System.Text;

namespace Overlord.Tests.Fixtures;

/// <summary>
/// Answers HTTP requests from queues of canned responses and records what was sent. A response
/// given a path answers only requests for that path, so parallel requests get the right answers;
/// the rest answer requests in the order they arrive.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _byPath = [];
    private readonly List<string> _requestBodies = [];

    public IReadOnlyList<string> RequestBodies
    {
        get
        {
            lock (_gate)
                return _requestBodies.ToList();
        }
    }

    public FakeHttpHandler Respond(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(null, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public FakeHttpHandler Fail(HttpStatusCode status, string? path = null) =>
        Enqueue(path, () => new HttpResponseMessage(status) { Content = new StringContent("") });

    public FakeHttpHandler RespondBytes(byte[] body, string? path = null) =>
        Enqueue(path, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    public FakeHttpHandler Throw(Exception exception) => Enqueue(null, () => throw exception);

    public int Remaining
    {
        get
        {
            lock (_gate)
                return _responses.Count + _byPath.Values.Sum(q => q.Count);
        }
    }

    private FakeHttpHandler Enqueue(string? path, Func<HttpResponseMessage> response)
    {
        lock (_gate)
        {
            if (path is null)
                _responses.Enqueue(response);
            else if (_byPath.TryGetValue(path, out var queue))
                queue.Enqueue(response);
            else
                _byPath[path] = new Queue<Func<HttpResponseMessage>>([response]);
        }
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Func<HttpResponseMessage> next;
        lock (_gate)
        {
            _requestBodies.Add(body);
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (_byPath.TryGetValue(path, out var queue) && queue.Count > 0)
                next = queue.Dequeue();
            else if (_responses.Count > 0)
                next = _responses.Dequeue();
            else
                throw new InvalidOperationException("No canned response left for " + request.RequestUri);
        }
        return next();
    }
}
