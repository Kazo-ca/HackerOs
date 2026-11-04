using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Backup;

/// <summary>Exports and atomically restores validated Kazo OS IndexedDB backups.</summary>
/// <remarks>
/// The backup is the whole OS database as one JSON document (<c>formatVersion: 1</c>, schema
/// version, every store, SHA-256 of the payload). ADR 0099 section 1 ships it as a gzip archive
/// with the <see cref="ArchiveExtension"/> extension (<see cref="ExportArchiveAsync"/>,
/// <see cref="ReadArchive"/>); restoring accepts the plain JSON too.
/// </remarks>
public sealed class IndexedDbBackupService : IAsyncDisposable
{
    /// <summary>File extension of a compressed OS backup (ADR 0099 section 1).</summary>
    public const string ArchiveExtension = ".hosbak";

    private const int FormatVersion = 1;
    private const string TransactionBoundary = "BackupRestore";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a backup service using the browser IndexedDB adapter.</summary>
    public IndexedDbBackupService(IJSRuntime runtime, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Exports every store in one read-only snapshot with a SHA-256 integrity digest.</summary>
    public async ValueTask<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        IndexedDbOperation[] operations = KazoOsIndexedDbSchema.ObjectStores
            .Select(store => new IndexedDbOperation("getAll", store.Name))
            .ToArray();
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            operations,
            cancellationToken).ConfigureAwait(false);

        JsonObject payload = BuildPayload(
            _timeProvider.GetUtcNow(),
            KazoOsIndexedDbSchema.ObjectStores.Select((store, index) => (store.Name, results[index])));
        JsonObject envelope = JsonNode.Parse(payload.ToJsonString())!.AsObject();
        envelope["sha256"] = ComputeHash(payload);
        return envelope.ToJsonString();
    }

    /// <summary>
    /// Exports the database like <see cref="ExportAsync"/> and compresses the JSON with gzip, the
    /// <c>.hosbak</c> archive downloaded to the host (ADR 0099 section 1).
    /// </summary>
    public async ValueTask<byte[]> ExportArchiveAsync(CancellationToken cancellationToken = default)
    {
        string json = await ExportAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Reads the backup JSON out of an archive: a gzip <c>.hosbak</c> is decompressed, anything
    /// else is read as the plain UTF-8 JSON of <see cref="ExportAsync"/>.
    /// </summary>
    /// <param name="archive">Bytes of the file the user picked.</param>
    /// <exception cref="IndexedDbBackupValidationException">The gzip stream is damaged.</exception>
    public static string ReadArchive(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length < 2 || archive[0] != 0x1F || archive[1] != 0x8B)
        {
            return Encoding.UTF8.GetString(archive);
        }

        try
        {
            using GZipStream gzip = new(new MemoryStream(archive, writable: false), CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or DecoderFallbackException)
        {
            throw new IndexedDbBackupValidationException("Backup archive is damaged (invalid gzip data).", exception);
        }
    }

    /// <summary>
    /// Validates a backup completely (format, schema version, stores, identities, SHA-256) without
    /// touching the database, so a caller can refuse a bad archive before stopping the session.
    /// </summary>
    /// <param name="backupJson">The backup JSON.</param>
    /// <exception cref="IndexedDbBackupValidationException">The backup is malformed, incompatible or corrupt.</exception>
    public static IndexedDbBackupSummary Inspect(string backupJson)
    {
        ValidatedBackup backup = Validate(backupJson);
        return new IndexedDbBackupSummary(
            backup.CreatedAtUtc,
            KazoOsIndexedDbSchema.CurrentVersion,
            backup.RecordCount,
            backup.Stores.ToDictionary(store => store.Name, store => store.Records.GetArrayLength(), StringComparer.Ordinal));
    }

    /// <summary>Validates a backup completely, then restores it using the explicitly selected mode.</summary>
    public async ValueTask<IndexedDbRestoreResult> RestoreAsync(
        string backupJson,
        IndexedDbRestoreMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ValidatedBackup backup = Validate(backupJson);
        List<IndexedDbOperation> operations = [];
        if (mode == IndexedDbRestoreMode.Replace)
        {
            operations.AddRange(KazoOsIndexedDbSchema.ObjectStores
                .Select(store => new IndexedDbOperation("clear", store.Name)));
        }

        // Replace writes into emptied stores; merge overwrites the records with the same key and
        // keeps every other one (ADR 0099 section 1). Either way it is one transaction.
        foreach ((string storeName, JsonElement records) in backup.Stores)
        {
            operations.AddRange(records.EnumerateArray().Select(record =>
                new IndexedDbOperation("put", storeName, Value: record.Clone())));
        }

        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            operations,
            cancellationToken).ConfigureAwait(false);

        return new IndexedDbRestoreResult(mode, backup.RecordCount);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private static ValidatedBackup Validate(string backupJson)
    {
        ArgumentNullException.ThrowIfNull(backupJson);
        if (string.IsNullOrWhiteSpace(backupJson))
        {
            throw new IndexedDbBackupValidationException("The file is empty or is not a Kazo OS backup.");
        }

        JsonObject envelope;
        try
        {
            envelope = JsonNode.Parse(backupJson)?.AsObject()
                ?? throw new JsonException("Backup root must be an object.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new IndexedDbBackupValidationException("Backup JSON is malformed.", exception);
        }

        int formatVersion = RequiredInt(envelope, "formatVersion");
        string databaseName = RequiredString(envelope, "databaseName");
        int schemaVersion = RequiredInt(envelope, "databaseSchemaVersion");
        string createdAt = RequiredString(envelope, "createdAtUtc");
        string expectedHash = RequiredString(envelope, "sha256");
        if (schemaVersion != KazoOsIndexedDbSchema.CurrentVersion)
        {
            // ADR 0099 section 1: a backup of another schema version is refused with a clear message.
            throw new IndexedDbBackupValidationException(
                $"This backup uses database schema version {schemaVersion}; this Kazo OS uses version {KazoOsIndexedDbSchema.CurrentVersion}. It cannot be restored here.");
        }

        if (formatVersion != FormatVersion
            || databaseName != KazoOsIndexedDbSchema.DatabaseName
            || !DateTimeOffset.TryParse(createdAt, out DateTimeOffset createdAtUtc)
            || expectedHash.Length != 64)
        {
            throw new IndexedDbBackupValidationException("Backup format or database schema is incompatible.");
        }

        JsonObject storesNode = envelope["stores"] as JsonObject
            ?? throw new IndexedDbBackupValidationException("Backup stores object is missing.");
        string[] expectedStores = KazoOsIndexedDbSchema.ObjectStores.Select(store => store.Name).ToArray();
        if (!storesNode.Select(item => item.Key).Order().SequenceEqual(expectedStores.Order(), StringComparer.Ordinal))
        {
            throw new IndexedDbBackupValidationException("Backup store set does not match the current schema.");
        }

        List<(string Name, JsonElement Records)> stores = [];
        foreach (IndexedDbObjectStoreDefinition definition in KazoOsIndexedDbSchema.ObjectStores)
        {
            JsonArray recordsNode = storesNode[definition.Name] as JsonArray
                ?? throw new IndexedDbBackupValidationException($"Store '{definition.Name}' must be an array.");
            JsonElement records = JsonDocument.Parse(recordsNode.ToJsonString()).RootElement.Clone();
            ValidateRecordIdentities(definition, records);
            stores.Add((definition.Name, records));
        }

        JsonObject payload = BuildPayload(createdAtUtc, stores.Select(store => (store.Name, store.Records)));
        string actualHash = ComputeHash(payload);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHash.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(actualHash)))
        {
            throw new IndexedDbBackupValidationException("Backup SHA-256 integrity validation failed.");
        }

        return new ValidatedBackup(stores, stores.Sum(store => store.Records.GetArrayLength()), createdAtUtc);
    }

    private static void ValidateRecordIdentities(IndexedDbObjectStoreDefinition definition, JsonElement records)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (JsonElement record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                throw new IndexedDbBackupValidationException($"Store '{definition.Name}' contains a non-object record.");
            }

            string identity = string.Join("\u001f", definition.KeyPath.Select(property =>
            {
                if (!record.TryGetProperty(property, out JsonElement value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    or JsonValueKind.Object or JsonValueKind.Array)
                {
                    throw new IndexedDbBackupValidationException(
                        $"Store '{definition.Name}' record is missing valid key property '{property}'.");
                }
                return value.GetRawText();
            }));
            if (!identities.Add(identity))
            {
                throw new IndexedDbBackupValidationException($"Store '{definition.Name}' contains duplicate identities.");
            }

            if (definition.Name == KazoOsIndexedDbSchema.CatalogStoreName
                && (!record.TryGetProperty("enabledFlag", out JsonElement enabledFlag)
                    || enabledFlag.ValueKind != JsonValueKind.Number
                    || enabledFlag.GetInt32() is not (0 or 1)))
            {
                throw new IndexedDbBackupValidationException("Catalog enabledFlag must be 0 or 1.");
            }
        }
    }

    private static JsonObject BuildPayload(
        DateTimeOffset createdAtUtc,
        IEnumerable<(string Name, JsonElement Records)> stores)
    {
        JsonObject storesNode = [];
        foreach ((string name, JsonElement records) in stores)
        {
            storesNode[name] = JsonNode.Parse(records.GetRawText());
        }

        return new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["databaseName"] = KazoOsIndexedDbSchema.DatabaseName,
            ["databaseSchemaVersion"] = KazoOsIndexedDbSchema.CurrentVersion,
            ["createdAtUtc"] = createdAtUtc.ToUniversalTime().ToString("O"),
            ["stores"] = storesNode
        };
    }

    private static string ComputeHash(JsonObject payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString())));

    private static string RequiredString(JsonObject value, string property) =>
        value[property]?.GetValue<string>()
        ?? throw new IndexedDbBackupValidationException($"Backup property '{property}' is missing.");

    private static int RequiredInt(JsonObject value, string property) =>
        value[property]?.GetValue<int>()
        ?? throw new IndexedDbBackupValidationException($"Backup property '{property}' is missing.");

    private sealed record ValidatedBackup(
        IReadOnlyList<(string Name, JsonElement Records)> Stores,
        int RecordCount,
        DateTimeOffset CreatedAtUtc);
}

/// <summary>Selects explicit non-destructive merge or destructive replacement restore semantics.</summary>
public enum IndexedDbRestoreMode
{
    /// <summary>Writes every backup record over the record with the same key and keeps the others.</summary>
    Merge = 1,

    /// <summary>Clears all stores and writes the validated backup in one transaction.</summary>
    Replace = 2
}

/// <summary>What a validated backup holds, read before restoring it.</summary>
/// <param name="CreatedAtUtc">When the backup was exported.</param>
/// <param name="SchemaVersion">IndexedDB schema version of the backup (always the current one once validated).</param>
/// <param name="RecordCount">Records in every store.</param>
/// <param name="StoreRecordCounts">Records per store name.</param>
public sealed record IndexedDbBackupSummary(
    DateTimeOffset CreatedAtUtc,
    int SchemaVersion,
    int RecordCount,
    IReadOnlyDictionary<string, int> StoreRecordCounts);

/// <summary>Reports the completed restore mode and number of backup records processed.</summary>
public sealed record IndexedDbRestoreResult(IndexedDbRestoreMode Mode, int RecordCount);

/// <summary>Represents malformed, incompatible, corrupt, or conflicting backup data.</summary>
public sealed class IndexedDbBackupValidationException : Exception
{
    /// <summary>Creates a validation failure.</summary>
    public IndexedDbBackupValidationException(string message) : base(message) { }

    /// <summary>Creates a validation failure preserving its underlying cause.</summary>
    public IndexedDbBackupValidationException(string message, Exception innerException)
        : base(message, innerException) { }
}