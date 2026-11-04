using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Catalog;

/// <summary>Persists authoritative build manifests and local app enablement in IndexedDB.</summary>
public sealed class IndexedDbAppCatalogRepository : IPersistentAppCatalogRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "CatalogEnablementChange";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes a browser-backed app catalog repository.</summary>
    public IndexedDbAppCatalogRepository(IJSRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PersistedAppCatalogEntry>> ReconcileAsync(
        IEnumerable<AppManifest> selectedManifests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedManifests);
        AppManifest[] selected = selectedManifests.OrderBy(manifest => manifest.Id, StringComparer.Ordinal).ToArray();
        if (selected.Select(manifest => manifest.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length)
        {
            throw new ArgumentException("Selected app manifests must have unique IDs.", nameof(selectedManifests));
        }

        foreach (AppManifest manifest in selected)
        {
            ManifestValidationResult validation = AppManifestValidator.Validate(manifest);
            if (!validation.IsValid)
            {
                throw new ArgumentException($"Selected manifest '{manifest.Id}' is invalid.", nameof(selectedManifests));
            }
        }

        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CatalogRecord> existing = await ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, CatalogRecord> existingById = existing.ToDictionary(record => record.AppId, StringComparer.Ordinal);
        HashSet<string> selectedIds = selected.Select(manifest => manifest.Id).ToHashSet(StringComparer.Ordinal);
        List<IndexedDbOperation> operations = [];

        foreach (CatalogRecord retained in existing.Where(record => !selectedIds.Contains(record.AppId)))
        {
            if (retained.EnabledFlag != 0)
            {
                operations.Add(new IndexedDbOperation(
                    "put",
                    KazoOsIndexedDbSchema.CatalogStoreName,
                    Value: retained with { EnabledFlag = 0 }));
            }
        }

        foreach (AppManifest manifest in selected)
        {
            int enabledFlag = existingById.TryGetValue(manifest.Id, out CatalogRecord? prior)
                ? prior.EnabledFlag
                : 1;
            CatalogRecord selectedRecord = CatalogRecord.FromDomain(manifest, enabledFlag);
            if (prior is null || prior != selectedRecord)
            {
                operations.Add(new IndexedDbOperation(
                    "put",
                    KazoOsIndexedDbSchema.CatalogStoreName,
                    Value: selectedRecord));
            }
        }

        if (operations.Count > 0)
        {
            await _adapter.ExecuteAsync(
                TransactionBoundary,
                IndexedDbTransactionMode.ReadWrite,
                operations,
                cancellationToken).ConfigureAwait(false);
        }

        return selected.Select(manifest => new PersistedAppCatalogEntry(
            manifest,
            !existingById.TryGetValue(manifest.Id, out CatalogRecord? prior) || prior.EnabledFlag == 1)).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetEnabledAsync(
        string appId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> read = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", KazoOsIndexedDbSchema.CatalogStoreName, Key: appId)],
            cancellationToken).ConfigureAwait(false);
        if (read.Single().ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        CatalogRecord record = CatalogRecord.FromJson(read.Single());
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation(
                "put",
                KazoOsIndexedDbSchema.CatalogStoreName,
                Value: record with { EnabledFlag = enabled ? 1 : 0 })],
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PersistedAppCatalogEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CatalogRecord> records = await ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        return records.OrderBy(record => record.AppId, StringComparer.Ordinal).Select(record => record.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private async ValueTask<IReadOnlyList<CatalogRecord>> ReadRecordsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAll", KazoOsIndexedDbSchema.CatalogStoreName)],
            cancellationToken).ConfigureAwait(false);
        JsonElement records = results.Single();
        if (records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted app catalog records must be returned as an array.");
        }

        return records.EnumerateArray().Select(CatalogRecord.FromJson).ToArray();
    }

    private sealed record CatalogRecord(
        [property: JsonPropertyName("appId")] string AppId,
        [property: JsonPropertyName("manifestJson")] string ManifestJson,
        [property: JsonPropertyName("enabledFlag")] int EnabledFlag)
    {
        internal static CatalogRecord FromDomain(AppManifest manifest, int enabledFlag) =>
            new(manifest.Id, AppManifestJsonSerializer.SerializeCanonical(manifest), enabledFlag);

        internal PersistedAppCatalogEntry ToDomain()
        {
            if (EnabledFlag is not 0 and not 1)
            {
                throw new InvalidDataException($"Persisted enabled flag '{EnabledFlag}' is invalid.");
            }

            AppManifest manifest = AppManifestJsonSerializer.DeserializeStrict(ManifestJson);
            if (!StringComparer.Ordinal.Equals(manifest.Id, AppId))
            {
                throw new InvalidDataException("Persisted catalog key does not match its manifest app ID.");
            }

            return new PersistedAppCatalogEntry(manifest, EnabledFlag == 1);
        }

        internal static CatalogRecord FromJson(JsonElement element) => new(
            element.GetProperty("appId").GetString()!,
            element.GetProperty("manifestJson").GetString()!,
            element.GetProperty("enabledFlag").GetInt32());
    }
}
