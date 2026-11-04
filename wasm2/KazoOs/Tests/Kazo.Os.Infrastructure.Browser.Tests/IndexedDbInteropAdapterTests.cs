using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Infrastructure.Browser.Storage;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies the browser interop boundary without requiring IndexedDB or a browser.</summary>
public sealed class IndexedDbInteropAdapterTests
{
    [Fact]
    public async Task OpenAsync_ImportsModuleOnceAndPassesCanonicalSchemaIdentity()
    {
        FakeJsObjectReference module = new();
        FakeJsRuntime runtime = new(module);
        await using IndexedDbInteropAdapter adapter = new(runtime);

        await adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent());
        await adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent());

        Assert.Single(runtime.Invocations);
        Assert.Equal("import", runtime.Invocations[0].Identifier);
        Assert.Equal(IndexedDbInteropAdapter.ModulePath, runtime.Invocations[0].Arguments[0]);
        Assert.Equal(2, module.Invocations.Count);
        JsInvocation open = module.Invocations[0];
        Assert.Equal("openDatabase", open.Identifier);
        Assert.Equal(HackerOsIndexedDbSchema.DatabaseName, open.Arguments[0]);
        Assert.Equal(HackerOsIndexedDbSchema.CurrentVersion, open.Arguments[1]);
        Assert.IsType<IndexedDbMigrationPlan>(open.Arguments[2]);
    }

    [Fact]
    public void MigrationPlan_ContainsAContiguousPathToCurrentVersion()
    {
        IndexedDbMigrationPlan plan = IndexedDbMigrationPlan.CreateCurrent();

        Assert.Equal(
            Enumerable.Range(1, HackerOsIndexedDbSchema.CurrentVersion),
            plan.Steps.Select(step => step.TargetVersion));
        Assert.Equal(HackerOsIndexedDbSchema.ObjectStores, plan.Steps[0].CreateObjectStores);
        IndexedDbIndexChange contentHashIndex = Assert.Single(plan.Steps[1].CreateIndexes);
        Assert.Equal(HackerOsIndexedDbSchema.FileSystemEntryStoreName, contentHashIndex.ObjectStoreName);
        Assert.Equal("contentHash", contentHashIndex.Index.Name);
    }

    [Fact]
    public void MigrationPlan_RejectsMissingVersionSteps()
    {
        Assert.Throws<ArgumentException>(() => new IndexedDbMigrationPlan(
        [
            new IndexedDbMigrationStep(
                HackerOsIndexedDbSchema.CurrentVersion + 1,
                [],
                [],
                [],
                [])
        ]));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnknownBoundaryBeforeImportingModule()
    {
        FakeJsRuntime runtime = new(new FakeJsObjectReference());
        await using IndexedDbInteropAdapter adapter = new(runtime);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await adapter.ExecuteAsync("Unknown", IndexedDbTransactionMode.ReadOnly, []));

        Assert.Empty(runtime.Invocations);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsOperationOutsideNamedBoundaryBeforeImportingModule()
    {
        FakeJsRuntime runtime = new(new FakeJsObjectReference());
        await using IndexedDbInteropAdapter adapter = new(runtime);
        IndexedDbOperation operation = new("get", HackerOsIndexedDbSchema.SettingsStoreName, Key: "settings-key");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await adapter.ExecuteAsync("UserAccountWrite", IndexedDbTransactionMode.ReadOnly, [operation]));

        Assert.Empty(runtime.Invocations);
    }

    [Fact]
    public async Task ExecuteAsync_PassesBoundaryModeAndOrderedOperationsInOneInvocation()
    {
        JsonElement firstResult = JsonDocument.Parse("{\"id\":\"one\"}").RootElement.Clone();
        JsonElement secondResult = JsonDocument.Parse("2").RootElement.Clone();
        FakeJsObjectReference module = new([firstResult, secondResult]);
        await using IndexedDbInteropAdapter adapter = new(new FakeJsRuntime(module));
        IndexedDbOperation[] operations =
        [
            new("get", HackerOsIndexedDbSchema.UserStoreName, Key: "one"),
            new("count", HackerOsIndexedDbSchema.UserStoreName)
        ];

        IReadOnlyList<JsonElement> results = await adapter.ExecuteAsync(
            "UserAccountWrite",
            IndexedDbTransactionMode.ReadOnly,
            operations);

        Assert.Equal(2, results.Count);
        JsInvocation execute = Assert.Single(module.Invocations);
        Assert.Equal("executeTransaction", execute.Identifier);
        Assert.Equal(HackerOsIndexedDbSchema.DatabaseName, execute.Arguments[0]);
        Assert.Equal(HackerOsIndexedDbSchema.CurrentVersion, execute.Arguments[1]);
        Assert.Equal([HackerOsIndexedDbSchema.UserStoreName], Assert.IsAssignableFrom<IReadOnlyList<string>>(execute.Arguments[2]));
        Assert.Equal("readonly", execute.Arguments[3]);
        Assert.Same(operations, execute.Arguments[4]);
    }

    [Fact]
    public async Task ExecuteAsync_PassesAtomicCompareAndPutAsOneOperation()
    {
        JsonElement committed = JsonDocument.Parse("{\"committed\":true,\"actualValue\":4}")
            .RootElement.Clone();
        FakeJsObjectReference module = new([committed]);
        await using IndexedDbInteropAdapter adapter = new(new FakeJsRuntime(module));
        object replacement = new { id = "settings", revision = 5 };
        IndexedDbOperation operation = new(
            "compareAndPut",
            HackerOsIndexedDbSchema.SettingsStoreName,
            Key: "settings",
            Value: replacement,
            CompareProperty: "revision",
            ExpectedValue: 4L);

        IReadOnlyList<JsonElement> results = await adapter.ExecuteAsync(
            "SettingsDocumentWrite",
            IndexedDbTransactionMode.ReadWrite,
            [operation]);

        Assert.True(results[0].GetProperty("committed").GetBoolean());
        JsInvocation execute = Assert.Single(module.Invocations);
        IndexedDbOperation sent = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(execute.Arguments[4]));
        Assert.Equal("revision", sent.CompareProperty);
        Assert.Equal(4L, sent.ExpectedValue);
        Assert.Same(replacement, sent.Value);
    }

    [Fact]
    public async Task ExecuteAsync_PassesRevisionAssertionBeforeDependentWrites()
    {
        JsonElement[] results =
        [
            JsonDocument.Parse("4").RootElement.Clone(),
            JsonDocument.Parse("null").RootElement.Clone()
        ];
        FakeJsObjectReference module = new(results);
        await using IndexedDbInteropAdapter adapter = new(new FakeJsRuntime(module));
        IndexedDbOperation[] operations =
        [
            new(
                "assertPropertyEquals",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: "parent",
                CompareProperty: "revision",
                ExpectedValue: 4L),
            new("add", HackerOsIndexedDbSchema.FileSystemLinkStoreName, Value: new { parentId = "parent" })
        ];

        await adapter.ExecuteAsync(
            "FileSystemMetadataMutation",
            IndexedDbTransactionMode.ReadWrite,
            operations);

        JsInvocation execute = Assert.Single(module.Invocations);
        IndexedDbOperation[] sent =
        [.. Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(execute.Arguments[4])];
        Assert.Equal("assertPropertyEquals", sent[0].Kind);
        Assert.Equal("revision", sent[0].CompareProperty);
        Assert.Equal(4L, sent[0].ExpectedValue);
        Assert.Equal("add", sent[1].Kind);
    }

    [Fact]
    public async Task ExecuteAsync_translates_quota_exhaustion_to_recoverable_storage_error()
    {
        FakeJsObjectReference module = new(exception: new JSException(
            "QuotaExceededError: The quota has been exceeded."));
        await using IndexedDbInteropAdapter adapter = new(new FakeJsRuntime(module));

        BrowserStorageQuotaException exception = await Assert.ThrowsAsync<BrowserStorageQuotaException>(async () =>
            await adapter.ExecuteAsync(
                "UserAccountWrite",
                IndexedDbTransactionMode.ReadWrite,
                [new IndexedDbOperation("put", HackerOsIndexedDbSchema.UserStoreName, Value: new { id = "user" })]));

        Assert.IsType<JSException>(exception.InnerException);
        Assert.Contains("not committed", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public List<JsInvocation> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add(new JsInvocation(identifier, args ?? []));
            return ValueTask.FromResult((TValue)module);
        }
    }

    private sealed class FakeJsObjectReference(
        JsonElement[]? transactionResults = null,
        JSException? exception = null) : IJSObjectReference
    {
        public List<JsInvocation> Invocations { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add(new JsInvocation(identifier, args ?? []));
            if (identifier == "executeTransaction")
            {
                if (exception is not null)
                {
                    throw exception;
                }

                return ValueTask.FromResult((TValue)(object)(transactionResults ?? []));
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed record JsInvocation(string Identifier, object?[] Arguments);
}
