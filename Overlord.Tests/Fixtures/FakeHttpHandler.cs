using System.Net;
using System.Text;

namespace Overlord.Tests.Fixtures;

/// <summary>Answers HTTP requests from a queue of canned responses and records what was sent.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<string> RequestBodies { get; } = [];

    public FakeHttpHandler Respond(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public FakeHttpHandler Fail(HttpStatusCode status)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent("") });
        return this;
    }

    public FakeHttpHandler RespondBytes(byte[] body)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    public int Remaining => _responses.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        if (_responses.Count == 0)
            throw new InvalidOperationException("No canned response left for " + request.RequestUri);
        return _responses.Dequeue()();
    }
}
