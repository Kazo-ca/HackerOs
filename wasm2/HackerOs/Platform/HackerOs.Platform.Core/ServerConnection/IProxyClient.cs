using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using HackerOs.Server.Contracts.Proxy;

namespace HackerOs.Platform.Core.ServerConnection;

/// <summary>
/// Thin HTTP wrapper over the optional server's proxy endpoints (ADR 0028). Takes only
/// <see cref="HackerOs.Server.Contracts"/> DTOs and primitives, matching <see cref="IAccountClient"/>'s
/// direct-injection-friendly, browser-independent shape.
/// </summary>
/// <remarks>
/// <see cref="ProxyHttpResponse"/>'s <c>BodyBase64</c> is populated only when the request sets
/// <see cref="ProxyHttpRequest.IncludeBody"/> — false by default, so callers that only need
/// reachability/status/headers (e.g. <c>curl -I</c>, <c>ping</c>) get the original metadata-only
/// shape unchanged. Callers needing the fetched body (a normal <c>curl</c> GET) set it explicitly;
/// the response is capped at the same <c>MaxResponseBytes</c> limit as every other proxy response.
/// </remarks>
public interface IProxyClient
{
    /// <summary>
    /// Has the server perform one real HTTP request on the app's behalf (<c>POST api/proxy/http</c>),
    /// subject to the server's proxy policy and the app's stored capability grant.
    /// </summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="request">The method, URL, headers, timeout and requesting app; <c>IncludeBody</c> asks for the body too.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>The status, headers and, when requested, the base64 body of the real response.</returns>
    /// <exception cref="ServerConnectionException">The server refused or failed the request; the message carries the proxy error code when there is one.</exception>
    Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
        Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a single-port TCP reachability probe (ADR 0035) — one connect attempt against one
    /// host:port, no data exchanged. Used by <c>nmap</c>'s real-network fallback.
    /// </summary>
    Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
        Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads the proxy limits the server applies to this device (<c>GET api/proxy/policy</c>).</summary>
    /// <param name="serverBaseUrl">The server's base URL; endpoint paths are resolved relative to it.</param>
    /// <param name="accessToken">The bearer token of the connected account.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Concurrency, payload, duration and redirect limits, allowed protocols and operator warnings.</returns>
    /// <exception cref="ServerConnectionException">The server answered with a non-success status.</exception>
    Task<ProxyPolicyResponse> GetPolicyAsync(
        Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens one relayed TCP stream or "connected" UDP socket (ADR 0102 §2, §3). The socket is
    /// connected on the server when this returns; disposing the channel closes it.
    /// </summary>
    /// <exception cref="ProxyStreamException">The server refused the stream (its code says why).</exception>
    /// <exception cref="ServerConnectionException">The server could not be reached.</exception>
    Task<IProxyStreamChannel> OpenStreamAsync(
        Uri serverBaseUrl, string accessToken, ProxyProtocol protocol, ProxyStreamRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This proxy client cannot open relayed streams.");
}

/// <summary>Default <see cref="IProxyClient"/> implementation backed by <see cref="HttpClient"/>.</summary>
/// <param name="httpClient">Client for the JSON endpoints.</param>
/// <param name="webSocketConnector">
/// Opens the WebSocket of a stream route; a <see cref="ClientWebSocket"/> (the browser's
/// <c>WebSocket</c> under WebAssembly) when omitted. Tests pass the test server's client.
/// </param>
public sealed class HttpProxyClient(
    HttpClient httpClient,
    Func<Uri, CancellationToken, Task<WebSocket>>? webSocketConnector = null) : IProxyClient
{
    /// <summary>Creates the client used by dependency injection.</summary>
    /// <param name="httpClient">Client for the JSON endpoints.</param>
    public HttpProxyClient(HttpClient httpClient)
        : this(httpClient, null)
    {
    }

    /// <summary>
    /// Relay requests this client keeps in flight at once (ADR 0210 §5). The server refuses more than
    /// <c>MaxConcurrentPerDevice</c> (8 by default) instead of queuing them, and a page loads all its
    /// images, scripts and style sheets at the same time: the extra ones wait here, as a browser holds
    /// at most 6 connections per host. Probes count too, as the server counts them in the same quota.
    /// </summary>
    public const int MaxConcurrentRequests = 6;

    /// <summary>How many times a request the server refused with <c>QUOTA_EXCEEDED</c> is tried again (ADR 0210 §5).</summary>
    public const int QuotaRetries = 3;

    // One queue for the whole OS: the server counts the requests of the device, not of an app.
    private readonly SemaphoreSlim _slots = new(MaxConcurrentRequests, MaxConcurrentRequests);

    /// <inheritdoc />
    public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
        Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
        QueueAsync(() => SendHttpRequestAsync(serverBaseUrl, accessToken, request, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
        Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
        QueueAsync(() => SendTcpProbeAsync(serverBaseUrl, accessToken, request, cancellationToken), cancellationToken);

    /// <summary>
    /// Runs one relay call once a slot is free; a <c>QUOTA_EXCEEDED</c> refusal (another tab of the
    /// same device, or an operator who set a lower limit) waits a little and tries again.
    /// </summary>
    private async Task<T> QueueAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (ServerConnectionException exception)
                when (exception.ProxyErrorCode == ProxyErrorCode.QuotaExceeded && attempt < QuotaRetries)
            {
                // Retried below, after the slot is released.
            }
            finally
            {
                _slots.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProxyHttpResponse> SendHttpRequestAsync(
        Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, new Uri(serverBaseUrl, "api/proxy/http"))
        {
            Content = JsonContent.Create(request, ProxyContractsJsonContext.Default.ProxyHttpRequest)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ProxyErrorResponse? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new ServerConnectionException(
                error is null
                    ? $"The proxy request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"The proxy request failed: {error.ErrorCode} — {error.Message}",
                null,
                error?.ErrorCode);
        }

        return (await response.Content.ReadFromJsonAsync(
            ProxyContractsJsonContext.Default.ProxyHttpResponse, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<ProxyTcpProbeResponse> SendTcpProbeAsync(
        Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, new Uri(serverBaseUrl, "api/proxy/tcp-probe"))
        {
            Content = JsonContent.Create(request, ProxyContractsJsonContext.Default.ProxyTcpProbeRequest)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ProxyErrorResponse? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new ServerConnectionException(
                error is null
                    ? $"The TCP probe request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"The TCP probe request failed: {error.ErrorCode} — {error.Message}",
                null,
                error?.ErrorCode);
        }

        return (await response.Content.ReadFromJsonAsync(
            ProxyContractsJsonContext.Default.ProxyTcpProbeResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<ProxyPolicyResponse> GetPolicyAsync(
        Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = new(HttpMethod.Get, new Uri(serverBaseUrl, "api/proxy/policy"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ServerConnectionException(
                $"Reading the proxy policy failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return (await response.Content.ReadFromJsonAsync(
            ProxyContractsJsonContext.Default.ProxyPolicyResponse, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IProxyStreamChannel> OpenStreamAsync(
        Uri serverBaseUrl, string accessToken, ProxyProtocol protocol, ProxyStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUrl);
        ArgumentNullException.ThrowIfNull(request);

        // The browser's WebSocket cannot carry an Authorization header: the token travels as the
        // access_token query parameter, which the server accepts on the two stream routes only.
        UriBuilder builder = new(new Uri(serverBaseUrl, ProxyStreamRoutes.PathOf(protocol)))
        {
            Scheme = serverBaseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = string.Join('&',
                $"{ProxyStreamRoutes.HostParameter}={Uri.EscapeDataString(request.Host)}",
                $"{ProxyStreamRoutes.PortParameter}={request.Port}",
                $"{ProxyStreamRoutes.AppIdParameter}={Uri.EscapeDataString(request.AppId)}",
                $"{ProxyStreamRoutes.AccessTokenParameter}={Uri.EscapeDataString(accessToken)}")
        };

        WebSocket socket;
        try
        {
            socket = webSocketConnector is not null
                ? await webSocketConnector(builder.Uri, cancellationToken).ConfigureAwait(false)
                : await ConnectClientWebSocketAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            throw new ServerConnectionException($"The relay stream could not be opened: {exception.Message}");
        }

        try
        {
            return await WebSocketProxyStreamChannel.OpenAsync(socket, protocol, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<ProxyErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(
                ProxyContractsJsonContext.Default.ProxyErrorResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
