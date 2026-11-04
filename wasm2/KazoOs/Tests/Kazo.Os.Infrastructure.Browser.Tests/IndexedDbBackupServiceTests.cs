using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Backup;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies versioned exports and explicit atomic restore semantics.</summary>
public sealed class IndexedDbBackupServiceTests
{
    [Fact]
    public async Task Export_and_replace_use_all_store_snapshot_then_clear_and_put_atomically()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));

        string backup = await service.ExportAsync();
        IndexedDbRestoreResult result = await service.RestoreAsync(backup, IndexedDbRestoreMode.Replace);

        using JsonDocument document = JsonDocument.Parse(backup);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(64, document.RootElement.GetProperty("sha256").GetString()!.Length);
        Assert.Equal(1, result.RecordCount);
        TransactionInvocation export = module.Transactions[0];
        Assert.Equal("readonly", export.Mode);
        Assert.Equal(KazoOsIndexedDbSchema.ObjectStores.Count, export.Operations.Count);
        Assert.All(export.Operations, operation => Assert.Equal("getAll", operation.Kind));
        TransactionInvocation restore = module.Transactions[1];
        Assert.Equal("readwrite", restore.Mode);
        Assert.Equal(KazoOsIndexedDbSchema.ObjectStores.Count, restore.Operations.Count(operation => operation.Kind == "clear"));
        Assert.Single(restore.Operations, operation => operation.Kind == "put");
    }

    [Fact]
    public async Task Restore_rejects_tampered_backup_before_write_interop()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();
        string tampered = backup.Replace("user-one", "user-two", StringComparison.Ordinal);

        await Assert.ThrowsAsync<IndexedDbBackupValidationException>(async () =>
            await service.RestoreAsync(tampered, IndexedDbRestoreMode.Merge));

        Assert.Single(module.Transactions);
        Assert.Equal("readonly", module.Transactions[0].Mode);
    }

    [Fact]
    public async Task Merge_puts_archive_records_over_existing_ones_without_clearing()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();

        IndexedDbRestoreResult result = await service.RestoreAsync(backup, IndexedDbRestoreMode.Merge);

        TransactionInvocation restore = module.Transactions[1];
        IndexedDbOperation operation = Assert.Single(restore.Operations);
        Assert.Equal("put", operation.Kind);
        Assert.Equal(IndexedDbRestoreMode.Merge, result.Mode);
    }

    [Fact]
    public async Task Archive_round_trips_through_gzip_and_plain_json_is_still_accepted()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));

        byte[] archive = await service.ExportArchiveAsync();
        string json = IndexedDbBackupService.ReadArchive(archive);

        Assert.Equal(0x1F, archive[0]);
        Assert.Equal(0x8B, archive[1]);
        IndexedDbBackupSummary summary = IndexedDbBackupService.Inspect(json);
        Assert.Equal(1, summary.RecordCount);
        Assert.Equal(1, summary.StoreRecordCounts[KazoOsIndexedDbSchema.UserStoreName]);
        Assert.Equal(json, IndexedDbBackupService.ReadArchive(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Damaged_gzip_archive_is_refused()
    {
        byte[] damaged = [0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02, 0x03];

        // A truncated stream either fails to decompress or yields no backup: both are refused.
        Assert.Throws<IndexedDbBackupValidationException>(
            () => IndexedDbBackupService.Inspect(IndexedDbBackupService.ReadArchive(damaged)));
    }

    [Fact]
    public async Task Backup_of_another_schema_version_is_refused_with_both_versions_named()
    {
        FakeIndexedDbModule module = new(CreateSnapshot());
        await using IndexedDbBackupService service = new(new FakeJsRuntime(module));
        string backup = await service.ExportAsync();
        string older = backup.Replace(
            $"\"databaseSchemaVersion\":{KazoOsIndexedDbSchema.CurrentVersion}",
            "\"databaseSchemaVersion\":3",
            StringComparison.Ordinal);

        IndexedDbBackupValidationException exception = Assert.Throws<IndexedDbBackupValidationException>(
            () => IndexedDbBackupService.Inspect(older));

        Assert.Contains("version 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"version {KazoOsIndexedDbSchema.CurrentVersion}", exception.Message, StringComparison.Ordinal);
    }

    private static JsonElement[] CreateSnapshot()
    {
        JsonElement empty = JsonDocument.Parse("[]").RootElement.Clone();
        JsonElement[] stores = Enumerable.Repeat(empty, KazoOsIndexedDbSchema.ObjectStores.Count).ToArray();
        int userIndex = KazoOsIndexedDbSchema.ObjectStores
            .Select((store, index) => (store, index))
            .Single(item => item.store.Name == KazoOsIndexedDbSchema.UserStoreName)
            .index;
        stores[userIndex] = JsonDocument.Parse("[{\"id\":\"user-one\",\"loginName\":\"alice\"}]")
            .RootElement.Clone();
        return stores;
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    private sealed class FakeIndexedDbModule(JsonElement[] snapshot) : IJSObjectReference
    {
        internal List<TransactionInvocation> Transactions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier != "executeTransaction")
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            object?[] arguments = args ?? [];
            TransactionInvocation invocation = new(
                Assert.IsType<string>(arguments[3]),
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(arguments[4]));
            Transactions.Add(invocation);
            return ValueTask.FromResult((TValue)(object)(invocation.Mode == "readonly" ? snapshot : []));
        }
    }

    private sealed record TransactionInvocation(
        string Mode,
        IReadOnlyList<IndexedDbOperation> Operations);
}