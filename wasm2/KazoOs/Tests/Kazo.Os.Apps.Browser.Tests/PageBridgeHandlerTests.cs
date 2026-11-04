using System.Text;
using Kazo.Os.Apps.Browser.Bridge;
using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;
using Kazo.Os.AppSdk.Net;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

public sealed class PageBridgeHandlerTests
{
    private static readonly Uri Page = new("https://intranet.hack/app/");

    private static (PageBridgeHandler Handler, PageSession Session, DevToolsLog Log, HackCookieContainer Cookies) Create(FakeBrowserTransport transport, Uri? page = null)
    {
        DevToolsLog log = new();
        BrowserResourceLoader loader = new(transport, log);
        HackCookieContainer cookies = new();
        return (new PageBridgeHandler(loader, cookies), new PageSession(page ?? Page, "t", loader, CancellationToken.None), log, cookies);
    }

    private static BridgeFetchRequest Fetch(string url, string method = "GET", string? body = null, string mode = "cors", string credentials = "same-origin", params string[][] headers) =>
        new(method, url, headers, body is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), "fetch", credentials, mode);

    [Fact]
    public async Task Same_origin_fetch_gets_status_headers_body_and_sends_cookies()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("https://intranet.hack/api/items", "application/json", "[1,2]", ("X-Total", "2"), ("Set-Cookie", "a=b"));
        var (handler, session, log, cookies) = Create(transport);
        cookies.Add(new HackCookie("intranet.hack", "sid", "42"));

        BridgeFetchReply reply = await handler.FetchAsync(session, Fetch("https://intranet.hack/api/items", "POST", "{\"x\":1}", headers: [["Content-Type", "application/json"], ["X-Req", "1"]]));

        Assert.False(reply.Error);
        Assert.Equal(200, reply.Status);
        Assert.Equal("basic", reply.Type);
        Assert.Equal("[1,2]", Encoding.UTF8.GetString(Convert.FromBase64String(reply.Body!)));
        Assert.Contains(reply.Headers!, header => header[0] == "X-Total");
        Assert.DoesNotContain(reply.Headers!, header => header[0] == "Set-Cookie");
        Assert.Equal("sid=42", reply.Cookies);

        BrowserRequest sent = Assert.Single(transport.Requests);
        Assert.True(sent.WithCredentials);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(sent.Body!));
        Assert.Contains(sent.Headers, header => header.Key == "Origin" && header.Value == "https://intranet.hack");
        Assert.Contains(sent.Headers, header => header.Key == "X-Req");
        Assert.Contains(log.Network, entry => entry.Kind == NetworkRequestKind.Fetch && entry.Method == "POST");
    }

    [Fact]
    public async Task Cross_origin_fetch_without_allow_origin_is_blocked_by_cors()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("https://api.hack/data", "application/json", "{}");
        var (handler, session, log, _) = Create(transport);

        BridgeFetchReply reply = await handler.FetchAsync(session, Fetch("https://api.hack/data"));

        Assert.True(reply.Error);
        Assert.Contains(log.Console, entry => entry.Level == ConsoleLevel.Error
            && entry.Message.Contains("has been blocked by CORS policy: No 'Access-Control-Allow-Origin'", StringComparison.Ordinal));
        BrowserRequest sent = Assert.Single(transport.Requests);
        Assert.False(sent.WithCredentials);
        Assert.Contains(sent.Headers, header => header.Key == "Origin");
    }

    [Fact]
    public async Task Cross_origin_fetch_with_a_wildcard_exposes_only_safelisted_headers()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport()
            .Serve("https://api.hack/data", "application/json", "{}", ("Access-Control-Allow-Origin", "*"), ("X-Secret", "1"), ("X-Shown", "2"), ("Access-Control-Expose-Headers", "X-Shown"));
        var (handler, session, _, _) = Create(transport);

        BridgeFetchReply reply = await handler.FetchAsync(session, Fetch("https://api.hack/data"));

        Assert.False(reply.Error);
        Assert.Equal("cors", reply.Type);
        Assert.Contains(reply.Headers!, header => header[0] == "Content-Type");
        Assert.Contains(reply.Headers!, header => header[0] == "X-Shown");
        Assert.DoesNotContain(reply.Headers!, header => header[0] == "X-Secret");
    }

    [Fact]
    public async Task Credentialed_cross_origin_fetch_needs_the_exact_origin_and_allow_credentials()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport()
            .Serve("https://api.hack/star", "text/plain", "x", ("Access-Control-Allow-Origin", "*"))
            .Serve("https://api.hack/exact", "text/plain", "x", ("Access-Control-Allow-Origin", "https://intranet.hack"), ("Access-Control-Allow-Credentials", "true"));
        var (handler, session, _, _) = Create(transport);

        Assert.True((await handler.FetchAsync(session, Fetch("https://api.hack/star", credentials: "include"))).Error);
        Assert.False((await handler.FetchAsync(session, Fetch("https://api.hack/exact", credentials: "include"))).Error);
        Assert.All(transport.Requests, request => Assert.True(request.WithCredentials));
    }

    [Fact]
    public async Task No_cors_cross_origin_fetch_is_opaque()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("https://api.hack/pixel", "image/gif", "GIF");
        var (handler, session, _, _) = Create(transport);

        BridgeFetchReply reply = await handler.FetchAsync(session, Fetch("https://api.hack/pixel", mode: "no-cors"));

        Assert.False(reply.Error);
        Assert.True(reply.Opaque);
        Assert.Null(reply.Body);
    }

    [Fact]
    public async Task Fetch_of_a_file_url_is_refused_even_from_a_file_page()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("file:///home/neo/data.json", "application/json", "{}");
        var (handler, session, log, _) = Create(transport, new Uri("file:///home/neo/index.html"));

        BridgeFetchReply reply = await handler.FetchAsync(session, Fetch("file:///home/neo/data.json"));

        Assert.True(reply.Error);
        Assert.Empty(transport.Requests);
        Assert.Contains(log.Console, entry => entry.Message.Contains("URL scheme \"file\" is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_network_error_rejects_the_fetch()
    {
        var (handler, session, _, _) = Create(new FakeBrowserTransport());

        BridgeFetchReply reply = await handler.FetchAsync(session, Fetch("https://intranet.hack/missing-host-route"));

        Assert.True(reply.Error);
        Assert.Equal("Failed to fetch", reply.Message);
    }

    [Fact]
    public void Document_cookie_assignments_add_replace_and_expire()
    {
        var (handler, _, _, cookies) = Create(new FakeBrowserTransport());
        DateTimeOffset now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("theme=dark", handler.ApplyDocumentCookie(Page, "theme=dark; Path=/; SameSite=Lax", now));
        Assert.Equal("lang=fr; theme=dark", handler.ApplyDocumentCookie(Page, "lang=fr", now));
        Assert.Equal("lang=fr; theme=light", handler.ApplyDocumentCookie(Page, "theme=light", now));
        Assert.Equal("theme=light", handler.ApplyDocumentCookie(Page, "lang=; Max-Age=0", now));
        Assert.Equal(string.Empty, handler.ApplyDocumentCookie(Page, "theme=x; Expires=Thu, 01 Jan 1970 00:00:00 GMT", now));
        Assert.Equal(0, cookies.Count);
    }

    [Fact]
    public void File_pages_have_no_cookies()
    {
        var (handler, _, _, cookies) = Create(new FakeBrowserTransport());
        Uri file = new("file:///home/neo/index.html");

        Assert.Equal(string.Empty, handler.ApplyDocumentCookie(file, "a=1", DateTimeOffset.UtcNow));
        Assert.Equal(0, cookies.Count);
    }

    [Fact]
    public async Task Resources_added_by_script_come_back_as_data_urls()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport()
            .Serve("https://intranet.hack/late.css", "text/css", "a{background:url(bg.png)}")
            .Serve("https://intranet.hack/bg.png", "image/png", [7]);
        var (handler, session, _, _) = Create(transport);

        BridgeResourceReply css = await handler.LoadResourceAsync(session, new BridgeResourceRequest("https://intranet.hack/late.css", "stylesheet"));
        BridgeResourceReply missing = await handler.LoadResourceAsync(session, new BridgeResourceRequest("https://intranet.hack/none.png", "image"));

        Assert.StartsWith("data:text/css;charset=utf-8;base64,", css.DataUrl);
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(css.DataUrl[(css.DataUrl.IndexOf(',') + 1)..]));
        Assert.Contains("url(\"data:image/png;base64,", decoded);
        Assert.Equal("data:,", missing.DataUrl);
    }
}
