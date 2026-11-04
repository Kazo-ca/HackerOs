using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.ServerConnection;

/// <summary>Persists the single per-device server connection record (ADR 0028) in IndexedDB.</summary>
public sealed class IndexedDbServerConnectionRepository : IServerConnectionRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "ServerConnectionUpdate";
    private const string RecordKey = "current";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes a browser-backed server connection repository.</summary>
    /// <param name="runtime">Browser JavaScript runtime used only by the internal storage adapter.</param>
    public IndexedDbServerConnectionRepository(IJSRuntime runtime)
    {
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask<ServerConnectionState?> GetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", KazoOsIndexedDbSchema.ServerConnectionStoreName, Key: RecordKey)],
            cancellationToken).ConfigureAwait(false);

        JsonElement result = results.Single();
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : ServerConnectionRecord.ToDomain(result);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(ServerConnectionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ServerConnectionRecord record = ServerConnectionRecord.FromDomain(state);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation("put", KazoOsIndexedDbSchema.ServerConnectionStoreName, Value: record)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation("delete", KazoOsIndexedDbSchema.ServerConnectionStoreName, Key: RecordKey)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private sealed record ServerConnectionRecord(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("accountId")] string AccountId,
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("serverBaseUrl")] string ServerBaseUrl,
        [property: JsonPropertyName("deviceFingerprint")] string DeviceFingerprint,
        [property: JsonPropertyName("refreshTokenOpaque")] string RefreshTokenOpaque,
        [property: JsonPropertyName("refreshTokenExpiresUtc")] long RefreshTokenExpiresUtcMs)
    {
        internal static ServerConnectionRecord FromDomain(ServerConnectionState state) => new(
            RecordKey,
            state.AccountId.ToString("N"),
            state.DeviceId.ToString("N"),
            state.ServerBaseUrl,
            state.DeviceFingerprint,
            state.RefreshTokenOpaque,
            state.RefreshTokenExpiresUtc.ToUnixTimeMilliseconds());

        internal static ServerConnectionState ToDomain(JsonElement element) => new(
            Guid.ParseExact(element.GetProperty("accountId").GetString()!, "N"),
            Guid.ParseExact(element.GetProperty("deviceId").GetString()!, "N"),
            element.GetProperty("serverBaseUrl").GetString()!,
            element.GetProperty("deviceFingerprint").GetString()!,
            element.GetProperty("refreshTokenOpaque").GetString()!,
            DateTimeOffset.FromUnixTimeMilliseconds(element.GetProperty("refreshTokenExpiresUtc").GetInt64()));
    }
}
