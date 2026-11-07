using System.Security.Claims;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Services;

namespace Kazo.Os.Server.Endpoints;

/// <summary>
/// Proxy endpoints — P5-PROXY-001 through P5-PROXY-007, ADR 0102.
/// All endpoints require authentication.
///
/// POST /api/proxy/http      → execute an HTTP proxy request (with its body, ADR 0102 §1)
/// POST /api/proxy/tcp-probe → execute a single-port TCP reachability probe (ADR 0035)
/// GET  /api/proxy/tcp       → WebSocket: one relayed TCP stream (ADR 0102 §2)
/// GET  /api/proxy/udp       → WebSocket: one relayed "connected" UDP socket (ADR 0102 §3)
/// GET  /api/proxy/policy    → read the effective relay policy (ADR 0102 §5)
/// POST /api/proxy/grants    → issue or withdraw the grant an OS administrator approved (ADR 0233)
/// </summary>
public static class ProxyEndpoints
{
    /// <summary>Registers proxy route groups on the application.</summary>
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var proxyGroup = app.MapGroup("/api/proxy").WithTags("Proxy").RequireAuthorization();

        proxyGroup.MapPost("/http", ExecuteHttpAsync).WithName("ProxyHttp")
            .WithSummary("Executes a server-validated HTTP proxy request.");
        proxyGroup.MapPost("/tcp-probe", ExecuteTcpProbeAsync).WithName("ProxyTcpProbe")
            .WithSummary("Executes a server-validated single-port TCP reachability probe.");
        proxyGroup.MapGet("/policy", GetPolicyAsync).WithName("GetProxyPolicy")
            .WithSummary("Returns current proxy quotas and policy for the authenticated device.");
        proxyGroup.MapPost("/grants", IssueGrantAsync).WithName("IssueProxyGrant")
            .WithSummary("Issues or withdraws the network.real.access grant an OS administrator approved, with its ports.");
        proxyGroup.MapGet("/tcp", (HttpContext context, IProxyService proxy) =>
                ProxyStreamWebSocketPump.AcceptAsync(context, proxy, ProxyProtocol.Tcp))
            .WithName("ProxyTcpStream")
            .WithSummary("Opens a relayed TCP stream over a WebSocket (binary frames are the byte stream).");
        proxyGroup.MapGet("/udp", (HttpContext context, IProxyService proxy) =>
                ProxyStreamWebSocketPump.AcceptAsync(context, proxy, ProxyProtocol.Udp))
            .WithName("ProxyUdpStream")
            .WithSummary("Opens a relayed connected UDP socket over a WebSocket (one binary frame per datagram).");

        return app;
    }

    private static async Task<IResult> ExecuteHttpAsync(
        ProxyHttpRequest request, IProxyService proxy, ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var deviceId = GetDeviceId(user);

        try
        {
            var response = await proxy.ExecuteHttpRequestAsync(accountId, deviceId, request, ct);
            return Results.Json(response, ProxyContractsJsonContext.Default.ProxyHttpResponse);
        }
        catch (ProxyRequestException ex)
        {
            var error = new ProxyErrorResponse(
                request.RequestId,
                ex.ErrorCode,
                ex.Message,
                Guid.NewGuid());

            return Results.Json(error, ProxyContractsJsonContext.Default.ProxyErrorResponse, statusCode: StatusCodeOf(ex.ErrorCode));
        }
        catch (OperationCanceledException)
        {
            var error = new ProxyErrorResponse(
                request.RequestId, ProxyErrorCode.Timeout,
                "The request was cancelled.", Guid.NewGuid());
            return Results.Json(error, ProxyContractsJsonContext.Default.ProxyErrorResponse,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> ExecuteTcpProbeAsync(
        ProxyTcpProbeRequest request, IProxyService proxy, ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var deviceId = GetDeviceId(user);

        try
        {
            var response = await proxy.ExecuteTcpProbeAsync(accountId, deviceId, request, ct);
            return Results.Json(response, ProxyContractsJsonContext.Default.ProxyTcpProbeResponse);
        }
        catch (ProxyRequestException ex)
        {
            var error = new ProxyErrorResponse(
                request.RequestId,
                ex.ErrorCode,
                ex.Message,
                Guid.NewGuid());

            return Results.Json(error, ProxyContractsJsonContext.Default.ProxyErrorResponse, statusCode: StatusCodeOf(ex.ErrorCode));
        }
        catch (OperationCanceledException)
        {
            var error = new ProxyErrorResponse(
                request.RequestId, ProxyErrorCode.Timeout,
                "The request was cancelled.", Guid.NewGuid());
            return Results.Json(error, ProxyContractsJsonContext.Default.ProxyErrorResponse,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> GetPolicyAsync(
        IProxyService proxy, ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var deviceId = GetDeviceId(user);
        var policy = await proxy.GetPolicyAsync(accountId, deviceId, ct);
        return Results.Json(policy, ProxyContractsJsonContext.Default.ProxyPolicyResponse);
    }

    private static async Task<IResult> IssueGrantAsync(
        ProxyGrantIssueRequest request, IProxyGrantIssuer issuer, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            ProxyGrantIssued issued = await issuer.IssueAsync(GetAccountId(user), GetDeviceId(user), request, ct);
            return Results.Json(issued, ProxyContractsJsonContext.Default.ProxyGrantIssued);
        }
        catch (ProxyRequestException ex)
        {
            var error = new ProxyErrorResponse(null, ex.ErrorCode, ex.Message, Guid.NewGuid());
            return Results.Json(error, ProxyContractsJsonContext.Default.ProxyErrorResponse, statusCode: StatusCodeOf(ex.ErrorCode));
        }
    }

    /// <summary>Maps a relay error code to its HTTP status.</summary>
    /// <param name="errorCode">One of <see cref="ProxyErrorCode"/>.</param>
    public static int StatusCodeOf(string errorCode) => errorCode switch
    {
        ProxyErrorCode.CapabilityDenied or ProxyErrorCode.GrantMissing or ProxyErrorCode.GrantIssuingDisabled => StatusCodes.Status403Forbidden,
        ProxyErrorCode.QuotaExceeded => StatusCodes.Status429TooManyRequests,
        ProxyErrorCode.Timeout => StatusCodes.Status504GatewayTimeout,
        ProxyErrorCode.PayloadTooLarge => StatusCodes.Status502BadGateway,
        ProxyErrorCode.RelayDisabled => StatusCodes.Status503ServiceUnavailable,
        ProxyErrorCode.ConnectionRefused or ProxyErrorCode.HostUnreachable => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status400BadRequest
    };

    internal static Guid GetAccountId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    internal static Guid GetDeviceId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("device_id")!);
}
