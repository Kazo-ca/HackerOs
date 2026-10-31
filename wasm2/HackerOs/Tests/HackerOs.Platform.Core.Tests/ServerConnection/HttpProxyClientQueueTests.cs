using System.Net;
using System.Net.Http.Json;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Server.Contracts.Proxy;
using Xunit;

namespace HackerOs.Platform.Core.Tests.ServerConnection;

/// <summary>
/// ADR 0210 §5: a real page loads many resources at once, the server refuses more than its quota
/// per device, so <see cref="HttpProxyClient"/> keeps a few in flight and retries a full quota.
/// </summary>
public sealed class HttpProxyClientQueueTests
{
    private static readonly Uri Server = new("https://server.hackeros.test/");

    [Fact]
    public async Task A_page_of_twenty_resources_never_has_more_than_six_relay_calls_in_flight()
    {
        CountingRelayHandler handler = new();
        HttpProxyClient client = new(new HttpClient(handler));

        ProxyHttpResponse[] responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            client.ExecuteHttpRequestAsync(Server, "token", Request($"https://example.com/{index}.png"))));

        Assert.All(responses, response => Assert.Equal(200, response.StatusCode));
        Assert.Equal(20, handler.Calls);
        Assert.InRange(handler.MaxInFlight, 1, HttpProxyClient.MaxConcurrentRequests);
    }

    [Fact]
    public async Task A_full_quota_is_retried_and_the_request_succeeds()
    {
        CountingRelayHandler handler = new() { QuotaRefusals = 2 };
        HttpProxyClient client = new(new HttpClient(handler));

        ProxyHttpResponse response = await client.ExecuteHttpRequestAsync(Server, "token", Request("https://example.com/"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_quota_still_full_after_the_retries_fails_with_its_code()
    {
        CountingRelayHandler handler = new() { QuotaRefusals = int.MaxValue };
        HttpProxyClient client = new(new HttpClient(handler));

        ServerConnectionException failure = await Assert.ThrowsAsync<ServerConnectionException>(
            () => client.ExecuteHttpRequestAsync(Server, "token", Request("https://example.com/")));

        Assert.Equal(ProxyErrorCode.QuotaExceeded, failure.ProxyErrorCode);
        Assert.Equal(HttpProxyClient.QuotaRetries + 1, handler.Calls);
    }

    [Fact]
    public async Task Another_refusal_is_not_retried_and_keeps_its_code()
    {
        CountingRelayHandler handler = new() { Refusal = ProxyErrorCode.BlockedPort };
        HttpProxyClient client = new(new HttpClient(handler));

        ServerConnectionException failure = await Assert.ThrowsAsync<ServerConnectionException>(
            () => client.ExecuteHttpRequestAsync(Server, "token", Request("https://example.com:8443/")));

        Assert.Equal(ProxyErrorCode.BlockedPort, failure.ProxyErrorCode);
        Assert.Equal(1, handler.Calls);
    }

    private static ProxyHttpRequest Request(string url) =>
        ProxyHttpRequest.Create(Guid.NewGuid(), url, "GET", [], null, null, 30, "org.hackeros.browser", includeBody: true);

    /// <summary>A relay endpoint that answers after a short delay and records how many calls overlap.</summary>
    private sealed class CountingRelayHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _calls;
        private int _maxInFlight;

        public int QuotaRefusals { get; init; }

        public string? Refusal { get; init; }

        public int Calls => _calls;

        public int MaxInFlight => _maxInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            int inFlight = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = _maxInFlight) < inFlight && Interlocked.CompareExchange(ref _maxInFlight, inFlight, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(20, cancellationToken);
                ProxyHttpRequest sent = (await request.Content!.ReadFromJsonAsync(ProxyContractsJsonContext.Default.ProxyHttpRequest, cancellationToken))!;
                if (Refusal is not null || call <= QuotaRefusals)
                {
                    string code = Refusal ?? ProxyErrorCode.QuotaExceeded;
                    return new HttpResponseMessage(code == ProxyErrorCode.QuotaExceeded ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadRequest)
                    {
                        Content = JsonContent.Create(new ProxyErrorResponse(sent.RequestId, code, "refused", Guid.NewGuid()),
                            ProxyContractsJsonContext.Default.ProxyErrorResponse)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new ProxyHttpResponse(sent.RequestId, 200, "OK", [], null, 0, sent.TargetUrl, 0, 1),
                        ProxyContractsJsonContext.Default.ProxyHttpResponse)
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
