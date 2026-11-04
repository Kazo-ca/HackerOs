using HackerOs.Server.Contracts.Hosting;
using HackerOs.Server.Contracts.Versioning;
using HackerOs.Server.Services;

namespace HackerOs.Server.Endpoints;

/// <summary>
/// Versioning endpoints — P5-SRV-001.
/// GET  /api/version         → current and supported API versions.
/// POST /api/version/check   → compatibility check for a specific client.
/// GET  /api/hackeros/host-info and GET /hackeros-host.json → host discovery (ADR 0134, ADR 0137).
/// </summary>
public static class VersionEndpoints
{
    // Current server software version.
    private const string ServerVersion = "1.0.0";

    // Minimum IndexedDB schema version the server will accept.
    private const int MinCompatiblePwaSchema = 2;

    private static readonly ApiVersionResponse CurrentVersionInfo = new(
        ServerVersion,
        CurrentApiVersion: "1.0",
        SupportedVersions:
        [
            new("1.0", "current", null, MinCompatiblePwaSchema, 99)
        ],
        MinCompatiblePwaSchema);

    /// <summary>Registers version route group on the application.</summary>
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/version").WithTags("Versioning");

        group.MapGet("/", GetVersionAsync)
            .WithName("GetApiVersion")
            .WithSummary("Returns supported API versions and PWA compatibility window.")
            .AllowAnonymous();

        group.MapPost("/check", CheckCompatibilityAsync)
            .WithName("CheckCompatibility")
            .WithSummary("Checks whether a specific client PWA schema and API version are compatible.")
            .AllowAnonymous();

        return app;
    }

    /// <summary>Registers <c>GET /api/hackeros/host-info</c> (ADR 0134 §1).</summary>
    public static IEndpointRouteBuilder MapHostInfoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostInfoRoutes.ApiPath, GetHostInfo)
            .WithName("GetHostInfo")
            .WithTags("Versioning")
            .WithSummary("Says that this origin is a Kazo OS server (OS host discovery).")
            .AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Answers <c>/hackeros-host.json</c> as this server (ADR 0137). The OS served from here reads that
    /// file next to its <c>index.html</c> to learn that a Kazo OS server delivered it. The Ecosystem
    /// PWA ships a static file at the same path (<c>isServer: false</c>) that MapStaticAssets also
    /// maps, so the answer is given by a middleware that runs before any endpoint: a static host
    /// says "no server" and this server says "server", both without a failing request.
    /// </summary>
    public static IApplicationBuilder UseHostDiscoveryFile(this IApplicationBuilder app) =>
        app.Use((context, next) =>
            HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/" + HostInfoRoutes.DiscoveryFile, StringComparison.OrdinalIgnoreCase)
                ? GetHostInfo(context).ExecuteAsync(context)
                : next(context));

    private static IResult GetHostInfo(HttpContext context)
    {
        // Never cached: the same URL is a static "no server" file when the OS is moved to a static host.
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new HostInfoResponse(HostInfoRoutes.ServerProduct, IsServer: true, ServerVersion, CurrentVersionInfo.CurrentApiVersion),
            HostingContractsJsonContext.Default.HostInfoResponse);
    }

    private static IResult GetVersionAsync() =>
        Results.Json(CurrentVersionInfo, VersioningContractsJsonContext.Default.ApiVersionResponse);

    private static IResult CheckCompatibilityAsync(CompatibilityCheckRequest request)
    {
        bool schemaOk = request.ClientPwaSchemaVersion >= MinCompatiblePwaSchema;
        var requestedVersion = CurrentVersionInfo.SupportedVersions
            .FirstOrDefault(v => v.Version == request.DesiredApiVersion);

        if (requestedVersion is null)
        {
            return Results.Json(
                new CompatibilityCheckResponse(false, "Requested API version is not supported.", false, false),
                VersioningContractsJsonContext.Default.CompatibilityCheckResponse);
        }

        bool sunset = requestedVersion.Status == "sunset";
        bool compatible = schemaOk && !sunset;

        string? reason = !schemaOk
            ? $"PWA schema {request.ClientPwaSchemaVersion} is below the minimum required schema {MinCompatiblePwaSchema}."
            : sunset
                ? $"API version {request.DesiredApiVersion} has been sunset."
                : null;

        return Results.Json(
            new CompatibilityCheckResponse(compatible, reason, !schemaOk, sunset),
            VersioningContractsJsonContext.Default.CompatibilityCheckResponse);
    }
}
