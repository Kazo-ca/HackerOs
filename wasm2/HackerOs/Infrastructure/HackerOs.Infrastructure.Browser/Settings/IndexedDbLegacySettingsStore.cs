using System.Text.Json;
using HackerOs.Infrastructure.Browser.Interop;
using HackerOs.Infrastructure.Browser.Schema;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Settings;

/// <summary>
/// Reads the retired IndexedDB <c>settings</c> object store so the one-time migration can copy it
/// into real files (ADR 0044 section 9).
/// </summary>
/// <remarks>
/// Read-only on purpose: the store is neither emptied nor deleted, so a failed migration can be
/// retried, and the IndexedDB schema version stays unchanged (see
/// <c>doc/en/wasm/dev/old/common-pitfalls.md</c> on IndexedDB versioning). Records whose shape is unexpected
/// are skipped rather than failing the whole read.
/// </remarks>
public sealed class IndexedDbLegacySettingsStore : ILegacySettingsStore, IAsyncDisposable
{
    private const string TransactionBoundary = "LegacySettingsRead";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Creates the reader.</summary>
    /// <param name="runtime">Browser JS runtime hosting the IndexedDB interop module.</param>
    public IndexedDbLegacySettingsStore(IJSRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<LegacySettingsRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAll", HackerOsIndexedDbSchema.SettingsStoreName)],
            cancellationToken).ConfigureAwait(false);

        List<LegacySettingsRecord> records = [];
        foreach (JsonElement element in results.Single().EnumerateArray())
        {
            if (TryRead(element, out LegacySettingsRecord? record))
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    /// <summary>Maps one stored record (fields <c>scope</c>, <c>appId</c>, <c>userId</c>, <c>documentId</c>, <c>content</c>).</summary>
    public static bool TryRead(JsonElement element, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LegacySettingsRecord? record)
    {
        record = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("scope", out JsonElement scope) || scope.ValueKind != JsonValueKind.Number
            || !element.TryGetProperty("documentId", out JsonElement documentId) || documentId.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String
            || !scope.TryGetInt32(out int scopeValue) || !Enum.IsDefined(typeof(SettingsScope), scopeValue))
        {
            return false;
        }

        record = new LegacySettingsRecord(
            (SettingsScope)scopeValue,
            GetOptionalString(element, "appId"),
            GetOptionalString(element, "userId"),
            documentId.GetString()!,
            content.GetString()!);
        return true;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
