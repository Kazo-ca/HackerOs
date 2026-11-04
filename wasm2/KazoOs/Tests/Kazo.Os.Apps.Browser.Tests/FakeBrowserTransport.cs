using System.Collections.Concurrent;
using System.Text;
using Kazo.Os.Apps.Browser.Net;

namespace Kazo.Os.Apps.Browser.Tests;

/// <summary>A scripted network: URL → response or error, recording every request.</summary>
internal sealed class FakeBrowserTransport : IBrowserTransport
{
    private readonly ConcurrentDictionary<string, Func<BrowserRequest, BrowserFetchResult>> _routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<BrowserRequest> Requests { get; } = new();

    public FakeBrowserTransport Serve(string url, string contentType, string body, params (string Name, string Value)[] headers) =>
        Serve(url, contentType, Encoding.UTF8.GetBytes(body), headers);

    public FakeBrowserTransport Serve(string url, string contentType, byte[] body, params (string Name, string Value)[] headers)
    {
        _routes[url] = request => BrowserFetchResult.Ok(new BrowserResponse(
            200, "OK", request.Url,
            [new("Content-Type", contentType), .. headers.Select(header => new KeyValuePair<string, string>(header.Name, header.Value))],
            body));
        return this;
    }

    public FakeBrowserTransport Status(string url, int status, string reason)
    {
        _routes[url] = request => BrowserFetchResult.Ok(new BrowserResponse(status, reason, request.Url, [new("Content-Type", "text/plain")], []));
        return this;
    }

    public FakeBrowserTransport Fail(string url, string code)
    {
        _routes[url] = _ => BrowserFetchResult.Fail(code);
        return this;
    }

    public Task<BrowserFetchResult> SendAsync(BrowserRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return Task.FromResult(_routes.TryGetValue(request.Url.AbsoluteUri, out var route)
            ? route(request)
            : BrowserFetchResult.Fail("ERR_NAME_NOT_RESOLVED"));
    }

    public int CountRequests(string url) => Requests.Count(request => request.Url.AbsoluteUri == url);
}
