using System.Text.Json.Serialization;

namespace Kazo.Os.Server.Contracts.Admin;

// =============================================================================
// Server Administration Contracts — P5-SRV-003
//
// These contracts define what operators and account holders can query about
// data ownership, retention, health, and export. They are NOT the server's
// internal storage model; they are the API surface for transparency and
// self-service data management.
// =============================================================================

/// <summary>
/// Server health response — returned on GET /health (unauthenticated, for load-balancer checks)
/// and GET /api/admin/health (authenticated, for operator diagnostics).
/// </summary>
/// <param name="Status">Overall server health: "healthy", "degraded", or "unhealthy".</param>
/// <param name="Version">Running server software version.</param>
/// <param name="Uptime">ISO-8601 duration since server start.</param>
/// <param name="Components">Per-component health entries (database, storage, proxy pool, etc.).</param>
public sealed record ServerHealthResponse(
    string Status,
    string Version,
    string Uptime,
    IReadOnlyList<ComponentHealth> Components);

/// <summary>
/// Health status for a single server component.
/// </summary>
/// <param name="Name">Component name (e.g., "database", "blobStorage", "proxyPool").</param>
/// <param name="Status">Component status: "healthy", "degraded", or "unhealthy".</param>
/// <param name="Message">Optional diagnostic message (operator-visible only).</param>
public sealed record ComponentHealth(
    string Name,
    string Status,
    string? Message);

/// <summary>
/// Account data summary — returned on GET /api/account/data-summary.
/// Allows users to understand what the server holds about them before export/deletion.
/// </summary>
/// <param name="AccountId">Account UUID.</param>
/// <param name="Username">Account username.</param>
/// <param name="CreatedUtc">Account creation date.</param>
/// <param name="RegisteredDevices">Number of registered devices.</param>
/// <param name="SyncedDomains">Domains with at least one synced record.</param>
/// <param name="TotalSyncedRecords">Total sync record count across all domains.</param>
/// <param name="StoredContentBytes">Total file content bytes stored on server.</param>
/// <param name="AuditEntryCount">Number of audit log entries for this account.</param>
/// <param name="DataRetentionPolicy">Human-readable data retention policy summary.</param>
/// <param name="EncryptionAtRest">True when server-side encryption at rest is enabled.</param>
public sealed record AccountDataSummaryResponse(
    Guid AccountId,
    string Username,
    DateTimeOffset CreatedUtc,
    int RegisteredDevices,
    IReadOnlyList<string> SyncedDomains,
    long TotalSyncedRecords,
    long StoredContentBytes,
    long AuditEntryCount,
    string DataRetentionPolicy,
    bool EncryptionAtRest);

/// <summary>
/// Account data export request — POST /api/account/export.
/// Queues a background export job; the client polls for completion.
/// </summary>
/// <param name="IncludeDomains">Domains to include. Null means all domains.</param>
/// <param name="IncludeFileContent">Whether to include file content blobs in the export.</param>
/// <param name="IncludeAuditLog">Whether to include the personal audit log.</param>
/// <param name="Format">Export format: "json" or "zip".</param>
public sealed record AccountDataExportRequest(
    IReadOnlyList<string>? IncludeDomains,
    bool IncludeFileContent,
    bool IncludeAuditLog,
    string Format);

/// <summary>
/// Export job status — returned on GET /api/account/export/{jobId}.
/// </summary>
/// <param name="JobId">Export job UUID.</param>
/// <param name="Status">Job status: "queued", "processing", "ready", or "failed".</param>
/// <param name="DownloadUrl">Presigned download URL. Non-null only when Status is "ready".</param>
/// <param name="ExpiresUtc">URL expiry. Non-null only when Status is "ready".</param>
/// <param name="FailureReason">Error description when Status is "failed".</param>
public sealed record AccountDataExportStatus(
    Guid JobId,
    string Status,
    string? DownloadUrl,
    DateTimeOffset? ExpiresUtc,
    string? FailureReason);

/// <summary>
/// Account deletion request — DELETE /api/account (requires explicit confirmation string).
/// The server exports a final backup before deletion; the download link is included in the response.
/// </summary>
/// <param name="ConfirmationPhrase">Must be exactly "DELETE MY ACCOUNT" to prevent accidental deletion.</param>
/// <param name="RetainExportDays">Days to retain the final export after deletion (1–30).</param>
public sealed record DeleteAccountRequest(
    string ConfirmationPhrase,
    int RetainExportDays);

/// <summary>
/// Account deletion response.
/// </summary>
/// <param name="Deleted">True when the deletion was accepted and queued.</param>
/// <param name="FinalExportDownloadUrl">Presigned URL for the final data export before deletion.</param>
/// <param name="ExportExpiresUtc">When the final export URL expires.</param>
public sealed record DeleteAccountResponse(
    bool Deleted,
    string FinalExportDownloadUrl,
    DateTimeOffset ExportExpiresUtc);

/// <summary>
/// Source-generated JSON context for admin/data contracts.
/// </summary>
[JsonSerializable(typeof(ServerHealthResponse))]
[JsonSerializable(typeof(ComponentHealth))]
[JsonSerializable(typeof(IReadOnlyList<ComponentHealth>))]
[JsonSerializable(typeof(AccountDataSummaryResponse))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(AccountDataExportRequest))]
[JsonSerializable(typeof(AccountDataExportStatus))]
[JsonSerializable(typeof(DeleteAccountRequest))]
[JsonSerializable(typeof(DeleteAccountResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class AdminContractsJsonContext : JsonSerializerContext { }
