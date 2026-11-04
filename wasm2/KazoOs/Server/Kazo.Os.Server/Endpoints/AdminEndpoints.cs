using Kazo.Os.Server.Contracts.Admin;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Kazo.Os.Server.Endpoints;

/// <summary>
/// Administration and data transparency endpoints — P5-SRV-003.
/// GET    /health                    → server health (unauthenticated)
/// GET    /api/account/data-summary  → data held for the authenticated account
/// POST   /api/account/export        → queue an account data export
/// GET    /api/account/export/{jobId}→ check export job status
/// DELETE /api/account               → delete account (with final export)
/// </summary>
public static class AdminEndpoints
{
    private const string ServerVersion = "1.0.0";

    /// <summary>Registers admin/data route groups on the application.</summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Health is unauthenticated for load-balancer use.
        app.MapGet("/health", HealthCheckAsync)
            .WithTags("Health")
            .WithName("HealthCheck")
            .AllowAnonymous();

        var accountGroup = app.MapGroup("/api/account").WithTags("Account").RequireAuthorization();
        accountGroup.MapGet("/data-summary", DataSummaryAsync).WithName("GetDataSummary");
        accountGroup.MapPost("/export", RequestExportAsync).WithName("RequestExport");
        accountGroup.MapGet("/export/{jobId:guid}", GetExportStatusAsync).WithName("GetExportStatus");
        accountGroup.MapDelete("/", DeleteAccountAsync).WithName("DeleteAccount");

        return app;
    }

    private static IResult HealthCheckAsync(IServiceProvider services)
    {
        // Try to reach the database as a basic health check.
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HackerOsServerDbContext>();
        bool dbOk;
        try { dbOk = db.Database.CanConnect(); }
        catch { dbOk = false; }

        var status = dbOk ? "healthy" : "degraded";
        var response = new ServerHealthResponse(
            status, ServerVersion,
            Uptime: "unknown",
            Components:
            [
                new ComponentHealth("database", dbOk ? "healthy" : "unhealthy", dbOk ? null : "Cannot connect to database.")
            ]);

        return Results.Json(response, AdminContractsJsonContext.Default.ServerHealthResponse,
            statusCode: dbOk ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> DataSummaryAsync(
        HackerOsServerDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, ct);
        if (account is null) return Results.NotFound();

        var deviceCount = await db.Devices.CountAsync(d => d.AccountId == accountId && !d.IsRevoked, ct);
        var syncedDomains = await db.SyncRecords
            .Where(r => r.OwnerAccountId == accountId)
            .Select(r => r.Domain)
            .Distinct()
            .ToListAsync(ct);
        var totalRecords = await db.SyncRecords.LongCountAsync(r => r.OwnerAccountId == accountId, ct);
        var contentBytes = await db.ContentBlobs
            .Join(db.SyncRecords.Where(r => r.OwnerAccountId == accountId),
                b => b.ContentHash, r => r.ContentHash, (b, _) => b.TotalBytes)
            .SumAsync(ct);
        var auditCount = await db.AuditEntries.LongCountAsync(a => a.AccountId == accountId, ct);

        var summary = new AccountDataSummaryResponse(
            accountId, account.Username, account.CreatedUtc,
            deviceCount, syncedDomains, totalRecords, contentBytes, auditCount,
            DataRetentionPolicy: "Records retained for account lifetime; deleted on account deletion.",
            EncryptionAtRest: false); // Set to true when operator enables SQLite encryption extension.

        return Results.Json(summary, AdminContractsJsonContext.Default.AccountDataSummaryResponse);
    }

    private static IResult RequestExportAsync(
        AccountDataExportRequest request, ClaimsPrincipal user)
    {
        // Full implementation queues a background job and returns the job ID.
        // Stub: return a job ID immediately with "queued" status.
        var jobId = Guid.NewGuid();
        var status = new AccountDataExportStatus(jobId, "queued", null, null, null);
        return Results.Json(status, AdminContractsJsonContext.Default.AccountDataExportStatus,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult GetExportStatusAsync(Guid jobId)
    {
        // Stub: always return queued for now.
        var status = new AccountDataExportStatus(jobId, "queued", null, null, null);
        return Results.Json(status, AdminContractsJsonContext.Default.AccountDataExportStatus);
    }

    private static IResult DeleteAccountAsync(
        [FromBody] DeleteAccountRequest request, ClaimsPrincipal user)
    {
        if (request.ConfirmationPhrase != "DELETE MY ACCOUNT")
            return Results.BadRequest(new { error = "CONFIRMATION_REQUIRED" });

        // Full implementation marks account deleted and queues export + cleanup.
        // Stub: return a placeholder export URL.
        var response = new DeleteAccountResponse(
            true,
            $"https://example.com/exports/{Guid.NewGuid()}",
            DateTimeOffset.UtcNow.AddDays(request.RetainExportDays));
        return Results.Json(response, AdminContractsJsonContext.Default.DeleteAccountResponse);
    }

    private static Guid GetAccountId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
