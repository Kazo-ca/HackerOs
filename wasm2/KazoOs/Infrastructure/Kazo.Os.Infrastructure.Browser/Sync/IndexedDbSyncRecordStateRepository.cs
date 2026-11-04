using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Sync;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Sync;

/// <summary>Persists per-(domain, recordId) sync tracking state (ADR 0029) in IndexedDB.</summary>
public sealed class IndexedDbSyncRecordStateRepository : ISyncRecordStateRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "SyncRecordStateUpdate";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes a browser-backed sync record state repository.</summary>
    /// <param name="runtime">Browser JavaScript runtime used only by the internal storage adapter.</param>
    public IndexedDbSyncRecordStateRepository(IJSRuntime runtime)
    {
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask<SyncRecordTrackingState?> GetAsync(
        string domain, Guid recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", HackerOsIndexedDbSchema.SyncRecordStateStoreName, Key: FormatKey(domain, recordId))],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : ToState(result);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(
        string domain, Guid recordId, SyncRecordTrackingState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ContentHash);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation(
                "put",
                HackerOsIndexedDbSchema.SyncRecordStateStoreName,
                Value: new SyncRecordStateRecord(
                    FormatKey(domain, recordId),
                    domain,
                    recordId.ToString("N"),
                    state.Revision,
                    state.ContentHash,
                    state.RelativePath,
                    state.LocalEntryId?.ToString("N"),
                    state.IsTombstone,
                    state.UserLogin))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SyncTrackedRecord>> ListAsync(string domain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAll", HackerOsIndexedDbSchema.SyncRecordStateStoreName, IndexName: "domain", Query: domain)],
            cancellationToken).ConfigureAwait(false);

        JsonElement rows = results.Single();
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<SyncTrackedRecord> records = [];
        foreach (JsonElement row in rows.EnumerateArray())
        {
            records.Add(new SyncTrackedRecord(Guid.ParseExact(row.GetProperty("recordId").GetString()!, "N"), ToState(row)));
        }

        return records;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string domain, Guid recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation("delete", HackerOsIndexedDbSchema.SyncRecordStateStoreName, Key: FormatKey(domain, recordId))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    /// <summary>Reads one row; the ADR 0101 fields are absent from rows written before it.</summary>
    private static SyncRecordTrackingState ToState(JsonElement row) => new(
        row.GetProperty("revision").GetInt64(),
        row.GetProperty("contentHash").GetString()!,
        OptionalString(row, "relativePath"),
        OptionalString(row, "localEntryId") is { } localId ? Guid.ParseExact(localId, "N") : null,
        row.TryGetProperty("isTombstone", out JsonElement tombstone) && tombstone.ValueKind == JsonValueKind.True,
        OptionalString(row, "userLogin"));

    private static string? OptionalString(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string FormatKey(string domain, Guid recordId) => $"{domain}|{recordId:N}";

    private sealed record SyncRecordStateRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("recordId")] string RecordId,
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("contentHash")] string ContentHash,
        [property: JsonPropertyName("relativePath")] string? RelativePath,
        [property: JsonPropertyName("localEntryId")] string? LocalEntryId,
        [property: JsonPropertyName("isTombstone")] bool IsTombstone,
        [property: JsonPropertyName("userLogin")] string? UserLogin);
}
