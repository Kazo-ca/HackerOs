using System.Security.Claims;
using Kazo.Os.Server.Contracts.Identity;
using Kazo.Os.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kazo.Os.Server.Endpoints;

/// <summary>
/// Identity and device management endpoints — P5-SRV-002 / D-017.
/// POST   /api/account              → create account
/// POST   /api/auth/login           → login
/// POST   /api/auth/refresh         → refresh token
/// POST   /api/devices              → register device (authenticated)
/// GET    /api/devices              → list devices (authenticated)
/// DELETE /api/devices/{deviceId}   → revoke device (authenticated)
/// </summary>
public static class IdentityEndpoints
{
    /// <summary>Registers identity route groups on the application.</summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var accountGroup = app.MapGroup("/api/account").WithTags("Identity");
        accountGroup.MapPost("/", CreateAccountAsync)
            .WithName("CreateAccount")
            .WithSummary("Creates a new server account and registers the first device.")
            .AllowAnonymous();

        var authGroup = app.MapGroup("/api/auth").WithTags("Identity");
        authGroup.MapPost("/login", LoginAsync)
            .WithName("Login")
            .AllowAnonymous();
        authGroup.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .AllowAnonymous();

        var deviceGroup = app.MapGroup("/api/devices").WithTags("Devices").RequireAuthorization();
        deviceGroup.MapPost("/", RegisterDeviceAsync).WithName("RegisterDevice");
        deviceGroup.MapGet("/", GetDevicesAsync).WithName("GetDevices");
        deviceGroup.MapDelete("/{deviceId:guid}", RevokeDeviceAsync).WithName("RevokeDevice");

        return app;
    }

    private static async Task<IResult> CreateAccountAsync(
        CreateAccountRequest request, IAccountService accounts, CancellationToken ct)
    {
        try
        {
            var response = await accounts.CreateAccountAsync(request, ct);
            return Results.Json(response, IdentityContractsJsonContext.Default.CreateAccountResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (InvalidOperationException ex) when (ex.Message == "USERNAME_TAKEN")
        {
            return Results.Conflict(new { error = "USERNAME_TAKEN" });
        }
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, IAccountService accounts, CancellationToken ct)
    {
        try
        {
            var response = await accounts.LoginAsync(request, ct);
            return Results.Json(response, IdentityContractsJsonContext.Default.LoginResponse);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> RefreshAsync(
        RefreshTokenRequest request, IAccountService accounts, CancellationToken ct)
    {
        try
        {
            var response = await accounts.RefreshAsync(request, ct);
            return Results.Json(response, IdentityContractsJsonContext.Default.RefreshTokenResponse);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> RegisterDeviceAsync(
        RegisterDeviceRequest request, IAccountService accounts,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var response = await accounts.RegisterDeviceAsync(accountId, request, ct);
        return Results.Json(response, IdentityContractsJsonContext.Default.RegisterDeviceResponse,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetDevicesAsync(
        IAccountService accounts, ClaimsPrincipal user,
        [FromHeader(Name = "X-Device-Fingerprint")] string? fingerprint,
        CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var devices = await accounts.GetDevicesAsync(accountId, fingerprint ?? string.Empty, ct);
        return Results.Json(devices, IdentityContractsJsonContext.Default.IReadOnlyListDeviceSummary);
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId, IAccountService accounts, ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        try
        {
            await accounts.RevokeDeviceAsync(accountId, deviceId, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "DEVICE_NOT_FOUND" });
        }
    }

    private static Guid GetAccountId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
