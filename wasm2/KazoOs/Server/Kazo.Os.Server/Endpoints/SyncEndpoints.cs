using System.Security.Claims;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Services;

namespace Kazo.Os.Server.Endpoints;

/// <summary>
/// Sync endpoints — P5-SYNC-001 through P5-SYNC-006.
/// All endpoints require authentication.
///
/// POST /api/sync/pull               → pull records after cursor
/// POST /api/sync/push               → push local records batch
/// POST /api/sync/conflicts/resolve  → resolve a detected conflict
/// POST /api/sync/content/upload     → initiate chunked content upload
/// GET  /api/sync/content/upload/{sessionId}/progress → query upload progress
/// PUT  /api/sync/content/upload/{sessionId}/chunks/{index} → upload a chunk
/// POST /api/sync/content/download   → initiate content download (metadata only)
/// GET  /api/sync/content/download/{contentHash}/chunks/{index} → download chunk
///   (content-addressed and immutable — keyed by hash directly, not a session; ADR 0030)
/// </summary>
public static class SyncEndpoints
{
    /// <summary>Registers sync route groups on the application.</summary>
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var syncGroup = app.MapGroup("/api/sync").WithTags("Sync").RequireAuthorization();

        syncGroup.MapPost("/pull", PullAsync).WithName("SyncPull");
        syncGroup.MapPost("/push", PushAsync).WithName("SyncPush");
        syncGroup.MapPost("/conflicts/resolve", ResolveConflictAsync).WithName("ResolveConflict");

        // Content transfer sub-group.
        var contentGroup = syncGroup.MapGroup("/content");
        contentGroup.MapPost("/upload", InitiateUploadAsync).WithName("InitiateUpload");
        contentGroup.MapGet("/upload/{sessionId}/progress", QueryUploadProgressAsync).WithName("QueryUploadProgress");
        contentGroup.MapPut("/upload/{sessionId}/chunks/{chunkIndex:int}", UploadChunkAsync).WithName("UploadChunk");
        contentGroup.MapPost("/download", InitiateDownloadAsync).WithName("InitiateDownload");
        contentGroup.MapGet("/download/{contentHash}/chunks/{chunkIndex:int}", DownloadChunkAsync).WithName("DownloadChunk");

        return app;
    }

    private static async Task<IResult> PullAsync(
        PullRequest request, ISyncService sync, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            var response = await sync.PullAsync(accountId, request, ct);
            return Results.Json(response, SyncContractsJsonContext.Default.PullResponse);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> PushAsync(
        PushRequest request, ISyncService sync, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            var deviceId = GetDeviceId(user);
            var response = await sync.PushAsync(accountId, deviceId, request, ct);
            return Results.Json(response, SyncContractsJsonContext.Default.PushResponse);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ResolveConflictAsync(
        ResolveSyncConflictRequest request, ISyncService sync, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            var response = await sync.ResolveConflictAsync(accountId, request, ct);
            return Results.Json(response, SyncContractsJsonContext.Default.ResolveSyncConflictResponse);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> InitiateUploadAsync(
        InitiateContentUploadRequest request, IContentBlobService blobs, ClaimsPrincipal user, CancellationToken ct)
    {
        var accountId = GetAccountId(user);
        var response = await blobs.InitiateUploadAsync(accountId, request, ct);
        return Results.Json(response, ContentTransferContractsJsonContext.Default.InitiateContentUploadResponse);
    }

    private static async Task<IResult> QueryUploadProgressAsync(
        string sessionId, IContentBlobService blobs, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            var request = new QueryUploadProgressRequest(sessionId);
            var response = await blobs.QueryProgressAsync(accountId, request, ct);
            return Results.Json(response, ContentTransferContractsJsonContext.Default.QueryUploadProgressResponse);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> UploadChunkAsync(
        string sessionId, int chunkIndex,
        IContentBlobService blobs, ClaimsPrincipal user,
        Microsoft.AspNetCore.Http.IFormFile chunk, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            using var ms = new MemoryStream();
            await chunk.CopyToAsync(ms, ct);
            await blobs.AcceptChunkAsync(accountId, sessionId, chunkIndex, ms.ToArray(), ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> InitiateDownloadAsync(
        InitiateContentDownloadRequest request, IContentBlobService blobs, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            var response = await blobs.InitiateDownloadAsync(accountId, request, ct);
            return Results.Json(response, ContentTransferContractsJsonContext.Default.InitiateContentDownloadResponse);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DownloadChunkAsync(
        string contentHash, int chunkIndex,
        IContentBlobService blobs, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var accountId = GetAccountId(user);
            var chunkData = await blobs.GetChunkAsync(accountId, contentHash, chunkIndex, ct);
            return Results.Bytes(chunkData, "application/octet-stream");
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static Guid GetAccountId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static Guid GetDeviceId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("device_id")!);
}
