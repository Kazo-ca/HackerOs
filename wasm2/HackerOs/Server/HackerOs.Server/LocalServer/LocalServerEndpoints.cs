using HackerOs.Server.Contracts.Identity;
using HackerOs.Server.Services;

namespace HackerOs.Server.LocalServer;

/// <summary>
/// Endpoints of the installable server (ADR 0191, <c>doc/en/wasm/dev/installable-server.md</c>).
/// POST /api/auth/pair               → links a new device with a one-time pairing code (§6)
/// GET  /api/hackeros/local-ca.crt   → the local certificate authority to approve on each device (§2)
/// </summary>
public static class LocalServerEndpoints
{
    /// <summary>Path of the local certificate authority.</summary>
    public const string LocalCaPath = "/api/hackeros/local-ca.crt";

    /// <summary>
    /// Configuration key of the local certificate authority file (PEM), written by the installer
    /// when the server listens on the local network in HTTPS.
    /// </summary>
    public const string LocalCaSettingKey = "HackerOs:LocalServer:CaCertificatePath";

    /// <summary>Registers the endpoints.</summary>
    /// <param name="app">The application.</param>
    public static IEndpointRouteBuilder MapLocalServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/pair", PairAsync)
            .WithTags("Identity")
            .WithName("PairDevice")
            .WithSummary("Links a new device to a server account with a one-time pairing code.")
            .AllowAnonymous();

        app.MapGet(LocalCaPath, GetLocalCertificateAuthority)
            .WithTags("Local server")
            .WithName("GetLocalCertificateAuthority")
            .WithSummary("Returns the local certificate authority of an installed server, to approve on each device.")
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> PairAsync(
        PairDeviceRequest request, PairingCodeStore codes, IAccountService accounts, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceFingerprint))
        {
            return Results.BadRequest(new { error = "INVALID_REQUEST" });
        }

        string? account;
        try
        {
            account = codes.Redeem(request.Code);
        }
        catch (PairingRateLimitedException)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (account is null)
        {
            return Results.Json(new { error = "INVALID_PAIRING_CODE" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        try
        {
            LoginResponse response = await accounts.PairDeviceAsync(account, request, ct);
            return Results.Json(response, IdentityContractsJsonContext.Default.LoginResponse);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { error = "INVALID_PAIRING_CODE" }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static IResult GetLocalCertificateAuthority(IConfiguration configuration, IWebHostEnvironment environment)
    {
        string? configured = configuration[LocalCaSettingKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Results.NotFound();
        }

        string path = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        return File.Exists(path)
            ? Results.File(path, "application/x-x509-ca-cert", "hackeros-local-ca.crt")
            : Results.NotFound();
    }
}
