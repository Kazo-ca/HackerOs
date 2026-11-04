using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Sync;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Sync;

/// <summary>Persists per-domain sync pull cursors (ADR 0029) in IndexedDB.</summary>
public sealed class IndexedDbSyncCursorRepository : ISyncCursorRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "SyncCursorUpdate";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes a browser-backed sync cursor repository.</summary>
    /// <param name="runtime">Browser JavaScript runtime used only by the internal storage adapter.</param>
    public IndexedDbSyncCursorRepository(IJSRuntime runtime)
    {
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetCursorAsync(string domain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", HackerOsIndexedDbSchema.SyncCursorStoreName, Key: domain)],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : result.GetProperty("cursor").GetString();
    }

    /// <inheritdoc />
    public async ValueTask SetCursorAsync(string domain, string? cursor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation("put", HackerOsIndexedDbSchema.SyncCursorStoreName, Value: new SyncCursorRecord(domain, cursor))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stored in the same store as the cursors, under the key <c>&lt;domain&gt;#pulledAt</c>, so the
    /// schema needs no new store (ADR 0101 section 1).
    /// </remarks>
    public async ValueTask<DateTimeOffset?> GetLastPulledAtAsync(string domain, CancellationToken cancellationToken = default)
    {
        string? value = await GetCursorAsync(PulledAtKey(domain), cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset pulledAt)
            ? pulledAt
            : null;
    }

    /// <inheritdoc />
    public ValueTask SetLastPulledAtAsync(string domain, DateTimeOffset pulledAtUtc, CancellationToken cancellationToken = default) =>
        SetCursorAsync(PulledAtKey(domain), pulledAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private static string PulledAtKey(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return domain + "#pulledAt";
    }

    private sealed record SyncCursorRecord(
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("cursor")] string? Cursor);
}
