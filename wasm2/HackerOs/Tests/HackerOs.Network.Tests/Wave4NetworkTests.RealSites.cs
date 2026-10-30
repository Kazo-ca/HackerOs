using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Net;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Server.Contracts.Proxy;
using Xunit;

namespace HackerOs.Network.Tests;

/// <summary>
/// ADR 0210 on the OS side: real sites through the relay, hop by hop, with their cookies and the
/// source of the response, as the browser uses them.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    [Fact]
    public async Task A_real_site_response_says_it_came_from_the_real_network()
    {
        ScriptedRelay relay = new();
        relay.On("https://real-external-site.example/", 200, "<h1>real</h1>", ("Content-Type", "text/html"));
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse response = await client.GetAsync("https://real-external-site.example/");

        Assert.Equal(HackHttpResponseSource.RealNetwork, response.Source);
        Assert.Equal("<h1>real</h1>", response.Content);
        Assert.Equal("text/html", response.ContentType);
        ProxyHttpRequest sent = Assert.Single(relay.Requests);
        Assert.False(sent.FollowRedirects);
    }

    [Fact]
    public async Task A_simulated_site_response_says_it_came_from_the_simulated_network()
    {
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpResponse response = await client.GetAsync("http://hackersearch.net/");

        Assert.Equal(HackHttpResponseSource.Simulated, response.Source);
    }

    [Fact]
    public async Task The_cookies_a_real_site_sets_go_back_on_the_next_request()
    {
        ScriptedRelay relay = new();
        relay.On("https://real-external-site.example/login", 200, "ok", ("Set-Cookie", "session=abc; Path=/; HttpOnly; Secure"));
        relay.On("https://real-external-site.example/account", 200, "welcome");
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        HackCookieContainer cookies = new();
        using HackHttpClient client = new(cookies);

        HackHttpResponse login = await client.GetAsync("https://real-external-site.example/login");
        await client.GetAsync("https://real-external-site.example/account");

        Assert.Contains(new HackCookie("real-external-site.example", "session", "abc"), login.SetCookies);
        Assert.Equal("session=abc", relay.CookieHeaderOf("https://real-external-site.example/account"));
    }

    [Fact]
    public async Task A_redirect_keeps_the_cookie_it_sets_and_sends_it_to_the_next_hop()
    {
        ScriptedRelay relay = new();
        relay.On("https://real-external-site.example/login", 302, null,
            ("Location", "/home"), ("Set-Cookie", "session=xyz; Path=/"));
        relay.On("https://real-external-site.example/home", 200, "home");
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        HackCookieContainer cookies = new();
        using HackHttpClient client = new(cookies);

        HackHttpResponse response = await client.GetAsync("https://real-external-site.example/login");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(1, response.RedirectCount);
        Assert.Equal(new Uri("https://real-external-site.example/home"), response.RequestUri);
        Assert.Equal("session=xyz", relay.CookieHeaderOf("https://real-external-site.example/home"));
        Assert.Contains(new HackCookie("real-external-site.example", "session", "xyz"), cookies.GetAllCookies());
    }

    [Fact]
    public async Task A_domain_cookie_is_sent_to_the_subdomains_of_its_domain_only()
    {
        ScriptedRelay relay = new();
        relay.On("https://accounts.real-external-site.example/", 200, "ok",
            ("Set-Cookie", "sid=1; Domain=.real-external-site.example"),
            ("Set-Cookie", "evil=1; Domain=other-site.example"));
        relay.On("https://www.real-external-site.example/", 200, "www");
        relay.On("https://other-site.example/", 200, "other");
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        HackCookieContainer cookies = new();
        using HackHttpClient client = new(cookies);

        await client.GetAsync("https://accounts.real-external-site.example/");
        await client.GetAsync("https://www.real-external-site.example/");
        await client.GetAsync("https://other-site.example/");

        Assert.Equal("sid=1", relay.CookieHeaderOf("https://www.real-external-site.example/"));
        Assert.Null(relay.CookieHeaderOf("https://other-site.example/"));
        Assert.Single(cookies.GetCookies(new Uri("https://www.real-external-site.example/")));
    }

    [Fact]
    public async Task An_expired_cookie_of_a_real_site_is_no_longer_sent()
    {
        ScriptedRelay relay = new();
        relay.On("https://real-external-site.example/logout", 302, null,
            ("Location", "/"), ("Set-Cookie", "session=; Max-Age=0"));
        relay.On("https://real-external-site.example/", 200, "bye");
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        HackCookieContainer cookies = new();
        cookies.Add(new HackCookie("real-external-site.example", "session", "abc"));
        using HackHttpClient client = new(cookies);

        await client.GetAsync("https://real-external-site.example/logout");

        Assert.Equal("session=abc", relay.CookieHeaderOf("https://real-external-site.example/logout"));
        Assert.Null(relay.CookieHeaderOf("https://real-external-site.example/"));
    }

    [Fact]
    public async Task Without_network_real_access_a_real_site_is_not_found_and_never_relayed()
    {
        ScriptedRelay relay = new();
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkRealAccess));
        using HackHttpClient client = new();

        HackHttpRequestException failure = await Assert.ThrowsAsync<HackHttpRequestException>(
            () => client.GetAsync("https://real-external-site.example/"));

        Assert.Equal(HackNetworkError.HostNotFound, failure.NetworkError);
        Assert.Empty(relay.Requests);
    }

    [Theory]
    [InlineData(ProxyErrorCode.BlockedPort, HackNetworkError.BlockedByAdministrator)]
    [InlineData(ProxyErrorCode.BlockedAddress, HackNetworkError.BlockedByAdministrator)]
    [InlineData(ProxyErrorCode.GrantMissing, HackNetworkError.BlockedByAdministrator)]
    [InlineData(ProxyErrorCode.Timeout, HackNetworkError.TimedOut)]
    [InlineData(ProxyErrorCode.QuotaExceeded, HackNetworkError.ConnectionFailed)]
    [InlineData("", HackNetworkError.ConnectionFailed)]
    public async Task A_relay_refusal_becomes_the_error_a_browser_shows(string errorCode, HackNetworkError expected)
    {
        UseServer(new ConnectedServerConnectionService(), new ScriptedRelay { Refusal = errorCode });
        EnterApp(new GrantAllCapabilityChecker());
        using HackHttpClient client = new();

        HackHttpRequestException failure = await Assert.ThrowsAsync<HackHttpRequestException>(
            () => client.GetAsync("https://real-external-site.example:8443/"));

        Assert.Equal(expected, failure.NetworkError);
        Assert.Equal(HackNetworkErrorCodes.ToCode(expected), failure.ErrorCode);
    }

    [Fact]
    public async Task A_cookie_for_another_site_a_top_level_domain_or_already_expired_is_dropped()
    {
        ScriptedRelay relay = new();
        relay.On("https://www.example.org/", 200, "ok",
            ("Set-Cookie", "a=1; Domain=org"),
            ("Set-Cookie", "b=1; Domain=evil.org"),
            ("Set-Cookie", "c=1; Domain=example.org"),
            ("Set-Cookie", "d=1; Expires=Thu, 01 Jan 1970 00:00:00 GMT"));
        UseServer(new ConnectedServerConnectionService(), relay);
        EnterApp(new GrantAllCapabilityChecker());
        HackCookieContainer cookies = new();
        using HackHttpClient client = new(cookies);

        await client.GetAsync("https://www.example.org/");

        Assert.Equal([new HackCookie(".example.org", "c", "1")], cookies.GetAllCookies());
    }

    /// <summary>
    /// A relay that answers each URL with a scripted response and records what it was sent; with
    /// <see cref="Refusal"/> it refuses every request with that proxy error code (empty: no code).
    /// </summary>
    private sealed class ScriptedRelay : IProxyClient
    {
        private readonly Dictionary<string, (int Status, string? Body, (string Name, string Value)[] Headers)> _answers = [];

        public List<ProxyHttpRequest> Requests { get; } = [];

        public string? Refusal { get; init; }

        public void On(string url, int status, string? body, params (string Name, string Value)[] headers) =>
            _answers[url] = (status, body, headers);

        public string? CookieHeaderOf(string url) =>
            Requests.Last(request => request.TargetUrl == url).Headers
                .FirstOrDefault(header => header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))?.Value;

        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Refusal is not null)
            {
                throw new ServerConnectionException("refused by the test relay", null, Refusal.Length == 0 ? null : Refusal);
            }

            (int status, string? body, (string Name, string Value)[] headers) = _answers.TryGetValue(request.TargetUrl, out var answer)
                ? answer
                : (404, null, []);
            byte[] bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
            return Task.FromResult(new ProxyHttpResponse(
                request.RequestId,
                status,
                string.Empty,
                [.. headers.Select(header => new ProxyHeader(header.Name, header.Value))],
                BodyHash: null,
                BodyBytes: bytes.Length,
                FinalUrl: request.TargetUrl,
                RedirectHops: 0,
                DurationMs: 3,
                BodyBase64: request.IncludeBody && bytes.Length > 0 ? Convert.ToBase64String(bytes) : null));
        }

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
