using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace HackerNews.Api.Tests;

/// <summary>Serves canned Hacker News JSON by request path and counts how often each path is hit.</summary>
internal sealed class FakeHackerNewsHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string> _responses = new();
    private readonly ConcurrentDictionary<string, int> _hits = new();

    public FakeHackerNewsHandler WithBestStories(params long[] ids)
        => With("/v0/beststories.json", $"[{string.Join(',', ids)}]");

    public FakeHackerNewsHandler WithItem(long id, string json)
        => With($"/v0/item/{id}.json", json);

    public FakeHackerNewsHandler With(string path, string json)
    {
        _responses[path] = json;
        return this;
    }

    public int Hits(string path) => _hits.GetValueOrDefault(path);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _hits.AddOrUpdate(path, 1, (_, count) => count + 1);

        var response = _responses.TryGetValue(path, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };

        return Task.FromResult(response);
    }
}
