using System.Text.Json.Serialization;

namespace HackerOs.Server.Contracts.Proxy;

// =============================================================================
// HTTP/TCP/UDP relay contracts — P5-PROXY-001 through P5-PROXY-006, ADR 0102
//
// The relay is an EXPLICIT opt-in feature. The OS only relays for an app holding the dangerous
// "network.real.access" capability, and the server checks on its own that the app is granted
// network.real.access (ADR 0102 §4): client capability decisions are NEVER trusted.
//
// Security rules enforced SERVER-SIDE, with their defaults (HackerOs:Proxy in appsettings.json):
//   - Loopback (127.0.0.0/8, ::1), link-local and cloud metadata (169.254.0.0/16, fe80::/10)
//     → always blocked
//   - Private ranges (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7) → blocked unless
//     AllowPrivateNetworks
//   - DNS rebinding protection: every answer validated, the chosen address pinned for the connect
//   - Ports: HTTP 80/443, TCP streams 80/443, UDP 53/123, unless AllowAllPorts
//   - Maximum redirect hops: 5; request and response bodies: 10 MiB each
//   - Maximum request duration: 30 seconds
//   - Maximum concurrent requests per device: 8; open TCP/UDP streams per device: 4
//   - Simulated-domain hostnames (.hackeros.local, .sim, .hackeros) → refused
//     (prevents simulated-network traffic from accidentally reaching this proxy — P5-PROXY-006)
// =============================================================================

/// <summary>
/// Supported proxy protocol types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProxyProtocol>))]
public enum ProxyProtocol
{
    /// <summary>Standard HTTP/1.1 and HTTP/2 proxy request.</summary>
    Http,

    /// <summary>HTTP CONNECT tunnel (for HTTPS or websocket upgrade).</summary>
    HttpConnect,

    /// <summary>Raw TCP connection proxied through the server.</summary>
    Tcp,

    /// <summary>UDP datagram proxied through the server.</summary>
    Udp
}

/// <summary>
/// Header entry in a proxy request or response.
/// </summary>
/// <param name="Name">Header field name (case-insensitive).</param>
/// <param name="Value">Header field value.</param>
public sealed record ProxyHeader(string Name, string Value);

/// <summary>
/// Proxy HTTP request contract.
/// </summary>
/// <param name="RequestId">Client-generated idempotency UUID for this request.</param>
/// <param name="Protocol">Proxy protocol to use.</param>
/// <param name="TargetUrl">Fully-qualified URL or "host:port" for CONNECT/TCP/UDP.</param>
/// <param name="HttpMethod">HTTP method (GET, POST, PUT, …). Null for non-HTTP protocols.</param>
/// <param name="Headers">Request headers. The server strips hop-by-hop and sensitive headers.</param>
/// <param name="BodyHash">SHA-256 hex hash of the request body. Null for bodiless methods.</param>
/// <param name="BodyBytes">Total body byte count. 0 for bodiless methods.</param>
/// <param name="TimeoutSeconds">Client-requested timeout (1–30; server clamps).</param>
/// <param name="AppId">Declaring app ID; server validates against stored capability grant.</param>
/// <param name="IncludeBody">
/// When true, the server base64-encodes the fetched response body directly into
/// <see cref="ProxyHttpResponse.BodyBase64"/> (subject to the same <c>MaxResponseBytes</c> cap as
/// every other proxy response). False for callers that only need status/headers (e.g. <c>curl -I</c>,
/// <c>ping</c>'s HEAD probe) — the default, so existing callers are unaffected.
/// </param>
/// <param name="Body">
/// The request body bytes (ADR 0102 §1), serialized as base64. The server checks that their length
/// is <paramref name="BodyBytes"/> and their SHA-256 is <paramref name="BodyHash"/> before sending
/// them; <see langword="null"/> for a request without a body.
/// </param>
/// <param name="ContentType">The body's <c>Content-Type</c>; <see langword="null"/> without a body.</param>
public sealed record ProxyHttpRequest(
    Guid RequestId,
    ProxyProtocol Protocol,
    string TargetUrl,
    string? HttpMethod,
    IReadOnlyList<ProxyHeader> Headers,
    string? BodyHash,
    long BodyBytes,
    int TimeoutSeconds,
    string AppId,
    bool IncludeBody = false,
    byte[]? Body = null,
    string? ContentType = null)
{
    /// <summary>
    /// Gets whether the server follows redirects itself, up to <c>MaxRedirectHops</c>: true by default,
    /// so older clients are unaffected. When false (ADR 0210 §3), the server answers the first response
    /// as is, a redirect included, with its <c>Location</c> and <c>Set-Cookie</c> headers: the OS
    /// follows the hops itself, so the cookies of every hop reach the app's cookie container and go
    /// back on the next hop.
    /// </summary>
    public bool FollowRedirects { get; init; } = true;

    /// <summary>
    /// Builds the request for <paramref name="body"/>, filling <see cref="BodyHash"/> and
    /// <see cref="BodyBytes"/> from the bytes so the server's integrity check passes.
    /// </summary>
    /// <param name="requestId">Client-generated idempotency UUID.</param>
    /// <param name="targetUrl">Fully-qualified target URL.</param>
    /// <param name="httpMethod">HTTP method.</param>
    /// <param name="headers">Request headers.</param>
    /// <param name="body">Body bytes, or <see langword="null"/>/empty for none.</param>
    /// <param name="contentType">The body's <c>Content-Type</c>.</param>
    /// <param name="timeoutSeconds">Client-requested timeout.</param>
    /// <param name="appId">Declaring app ID.</param>
    /// <param name="includeBody">Whether the response body is wanted.</param>
    public static ProxyHttpRequest Create(
        Guid requestId,
        string targetUrl,
        string httpMethod,
        IReadOnlyList<ProxyHeader> headers,
        byte[]? body,
        string? contentType,
        int timeoutSeconds,
        string appId,
        bool includeBody)
    {
        bool hasBody = body is { Length: > 0 };
        return new ProxyHttpRequest(
            requestId,
            ProxyProtocol.Http,
            targetUrl,
            httpMethod,
            headers,
            hasBody ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body!)).ToLowerInvariant() : null,
            hasBody ? body!.Length : 0,
            timeoutSeconds,
            appId,
            includeBody,
            hasBody ? body : null,
            hasBody ? contentType : null);
    }
}

/// <summary>
/// Normalized proxy HTTP response. Metadata is always present; <see cref="BodyBase64"/> is
/// populated only when the request set <see cref="ProxyHttpRequest.IncludeBody"/>.
/// </summary>
/// <param name="RequestId">Matching request UUID.</param>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="ReasonPhrase">HTTP reason phrase.</param>
/// <param name="Headers">Response headers (server removes hop-by-hop headers).</param>
/// <param name="BodyHash">SHA-256 hex hash of the response body. Null when no body.</param>
/// <param name="BodyBytes">Response body size in bytes. 0 when no body.</param>
/// <param name="FinalUrl">URL after redirect resolution (may differ from TargetUrl).</param>
/// <param name="RedirectHops">Number of server-side redirects followed.</param>
/// <param name="DurationMs">Server-measured round-trip time in milliseconds.</param>
/// <param name="BodyBase64">
/// Base64-encoded response body, present only when the request asked for it via
/// <see cref="ProxyHttpRequest.IncludeBody"/> and the response actually had a body. Null otherwise —
/// including for metadata-only requests, where this field is never populated regardless of body size.
/// </param>
public sealed record ProxyHttpResponse(
    Guid RequestId,
    int StatusCode,
    string ReasonPhrase,
    IReadOnlyList<ProxyHeader> Headers,
    string? BodyHash,
    long BodyBytes,
    string FinalUrl,
    int RedirectHops,
    long DurationMs,
    string? BodyBase64 = null);

/// <summary>
/// Server-observed outcome of a single-port TCP connect probe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProxyTcpProbeState>))]
public enum ProxyTcpProbeState
{
    /// <summary>The TCP handshake completed — the port accepted a connection.</summary>
    Open,

    /// <summary>The target actively refused the connection (RST).</summary>
    Closed,

    /// <summary>No response before the timeout — likely firewalled or unreachable.</summary>
    Filtered
}

/// <summary>
/// A single-port TCP reachability probe (ADR 0035) — deliberately narrower than the HTTP proxy:
/// exactly one host, one port, one connect attempt, no data exchanged. This is not a port-range
/// scanner; <c>nmap</c>'s real-network fallback only ever issues one of these per invocation, for
/// the single port the user explicitly requested with <c>-p</c>.
/// </summary>
/// <param name="RequestId">Client-generated idempotency UUID for this request.</param>
/// <param name="Host">Target hostname or IP literal.</param>
/// <param name="Port">Target TCP port (1–65535). Not restricted to the HTTP proxy's port allow-list.</param>
/// <param name="TimeoutSeconds">Client-requested timeout (1–5; server clamps).</param>
/// <param name="AppId">Declaring app ID; recorded for audit.</param>
public sealed record ProxyTcpProbeRequest(
    Guid RequestId,
    string Host,
    int Port,
    int TimeoutSeconds,
    string AppId);

/// <summary>Result of a single-port TCP connect probe.</summary>
/// <param name="RequestId">Matching request UUID.</param>
/// <param name="State">Observed reachability state.</param>
/// <param name="DurationMs">Server-measured probe duration in milliseconds.</param>
public sealed record ProxyTcpProbeResponse(
    Guid RequestId,
    ProxyTcpProbeState State,
    long DurationMs);

/// <summary>
/// Reason codes returned when the server blocks or rejects a proxy request.
/// These are surfaced in ProxyErrorResponse for structured client handling.
/// </summary>
public static class ProxyErrorCode
{
    /// <summary>Target resolves to a blocked address (loopback, link-local, private, metadata).</summary>
    public const string BlockedAddress = "BLOCKED_ADDRESS";

    /// <summary>Target port is not permitted.</summary>
    public const string BlockedPort = "BLOCKED_PORT";

    /// <summary>Maximum redirect hops exceeded.</summary>
    public const string TooManyRedirects = "TOO_MANY_REDIRECTS";

    /// <summary>Response payload exceeds the configured size limit.</summary>
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";

    /// <summary>Request duration exceeded the timeout.</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>Device has exceeded the concurrent request quota.</summary>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>The device is not registered to the account, or no valid app identifier was given.</summary>
    public const string CapabilityDenied = "CAPABILITY_DENIED";

    /// <summary>
    /// The server does not grant <c>network.real.access</c> to the app for this account
    /// (ADR 0102 §4); answered with <c>403</c>.
    /// </summary>
    public const string GrantMissing = "GRANT_MISSING";

    /// <summary>The operator turned the relay off (<c>HackerOs:Proxy:Enabled</c> is <c>false</c>); <c>503</c>.</summary>
    public const string RelayDisabled = "RELAY_DISABLED";

    /// <summary>The request body does not match its announced size or SHA-256 hash.</summary>
    public const string BodyMismatch = "BODY_MISMATCH";

    /// <summary>The target actively refused the TCP connection (RST).</summary>
    public const string ConnectionRefused = "CONNECTION_REFUSED";

    /// <summary>The target did not answer the connection attempt (unreachable or filtered).</summary>
    public const string HostUnreachable = "HOST_UNREACHABLE";

    /// <summary>The target hostname ends in a simulated-domain suffix (.hackeros.local, .sim).</summary>
    public const string SimulatedDomainBlocked = "SIMULATED_DOMAIN_BLOCKED";

    /// <summary>DNS rebinding protection: the address resolved differently after TTL.</summary>
    public const string DnsRebindingBlocked = "DNS_REBINDING_BLOCKED";

    /// <summary>The request was malformed or contained an unsupported protocol variant.</summary>
    public const string MalformedRequest = "MALFORMED_REQUEST";
}

/// <summary>
/// Error response returned when the server rejects or cannot complete a proxy request.
/// </summary>
/// <param name="RequestId">Matching request UUID. Null when the request could not be parsed.</param>
/// <param name="ErrorCode">Stable error code from <see cref="ProxyErrorCode"/>.</param>
/// <param name="Message">Human-readable error description (may be shown in terminal output).</param>
/// <param name="AuditId">Server-side audit log entry UUID for operator correlation.</param>
public sealed record ProxyErrorResponse(
    Guid? RequestId,
    string ErrorCode,
    string Message,
    Guid AuditId);

/// <summary>
/// Proxy quota and policy status — returned on GET /api/proxy/policy for the authenticated device.
/// </summary>
/// <param name="DeviceId">The queried device.</param>
/// <param name="MaxConcurrentRequests">Configured concurrency limit.</param>
/// <param name="CurrentConcurrentRequests">Currently active proxy requests from this device.</param>
/// <param name="MaxPayloadBytes">Maximum response payload size in bytes.</param>
/// <param name="MaxDurationSeconds">Maximum allowed request duration.</param>
/// <param name="MaxRedirectHops">Maximum redirect hops the server will follow.</param>
/// <param name="AllowedProtocols">List of enabled protocols for this device.</param>
/// <param name="AuditRetentionDays">Number of days proxy audit entries are retained.</param>
/// <param name="OperatorWeakeningWarnings">Non-empty when the operator has disabled default safety rules.</param>
/// <param name="Enabled">Whether the relay is on (<c>HackerOs:Proxy:Enabled</c>).</param>
/// <param name="HttpPorts">Ports an HTTP request may target; empty when <paramref name="AllowAllPorts"/>.</param>
/// <param name="TcpPorts">Ports a TCP stream may target; empty when <paramref name="AllowAllPorts"/>.</param>
/// <param name="UdpPorts">Ports a UDP stream may target; empty when <paramref name="AllowAllPorts"/>.</param>
/// <param name="AllowAllPorts">Whether every port is allowed ("tout autoriser").</param>
/// <param name="AllowPrivateNetworks">Whether private ranges (the server's local network) are reachable.</param>
/// <param name="QuotasEnabled">Whether size, concurrency, stream and idle limits apply.</param>
/// <param name="MaxRequestBytes">Maximum request body size in bytes.</param>
/// <param name="MaxStreamsPerDevice">Maximum TCP/UDP streams open at once for one device.</param>
/// <param name="CurrentStreams">TCP/UDP streams this device has open now.</param>
/// <param name="StreamIdleTimeoutSeconds">Seconds without traffic after which a stream is closed.</param>
/// <param name="GrantedApps">App ids the server grants <c>network.real.access</c> to on its own (ADR 0102 §4).</param>
public sealed record ProxyPolicyResponse(
    Guid DeviceId,
    int MaxConcurrentRequests,
    int CurrentConcurrentRequests,
    long MaxPayloadBytes,
    int MaxDurationSeconds,
    int MaxRedirectHops,
    IReadOnlyList<string> AllowedProtocols,
    int AuditRetentionDays,
    IReadOnlyList<string> OperatorWeakeningWarnings,
    bool Enabled = true,
    IReadOnlyList<int>? HttpPorts = null,
    IReadOnlyList<int>? TcpPorts = null,
    IReadOnlyList<int>? UdpPorts = null,
    bool AllowAllPorts = false,
    bool AllowPrivateNetworks = false,
    bool QuotasEnabled = true,
    long MaxRequestBytes = 0,
    int MaxStreamsPerDevice = 0,
    int CurrentStreams = 0,
    int StreamIdleTimeoutSeconds = 0,
    IReadOnlyList<string>? GrantedApps = null);

/// <summary>
/// Source-generated JSON context for proxy contracts.
/// </summary>
[JsonSerializable(typeof(ProxyHttpRequest))]
[JsonSerializable(typeof(ProxyHttpResponse))]
[JsonSerializable(typeof(ProxyTcpProbeRequest))]
[JsonSerializable(typeof(ProxyTcpProbeResponse))]
[JsonSerializable(typeof(ProxyErrorResponse))]
[JsonSerializable(typeof(ProxyPolicyResponse))]
[JsonSerializable(typeof(ProxyHeader))]
[JsonSerializable(typeof(ProxyStreamOpened))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
[JsonSerializable(typeof(IReadOnlyList<ProxyHeader>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class ProxyContractsJsonContext : JsonSerializerContext { }
