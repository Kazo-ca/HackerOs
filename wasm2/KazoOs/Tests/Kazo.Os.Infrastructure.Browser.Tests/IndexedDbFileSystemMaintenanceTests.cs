using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies bounded and race-safe orphan content collection.</summary>
public sealed class IndexedDbFileSystemMaintenanceTests
{
    [Fact]
    public async Task CleanupAsync_DeletesOldOrphanButRetainsConcurrentlyReferencedHash()
    {
        long oldUtcMs = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        ScriptedModule module = new(
            One($"[{{\"contentHash\":\"orphan\",\"chunkIndex\":0,\"dataBase64\":\"\",\"createdUtcMs\":{oldUtcMs}}}," +
                $"{{\"contentHash\":\"used\",\"chunkIndex\":0,\"dataBase64\":\"\",\"createdUtcMs\":{oldUtcMs}}}]"),
            [Parse("{\"deleted\":1,\"referenced\":false}"), Parse("{\"deleted\":0,\"referenced\":true}")]);
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        IndexedDbFileContentCleanupResult result = await maintenance.CleanupAsync();

        Assert.Equal(2, result.CandidateHashes);
        Assert.Equal(1, result.DeletedChunks);
        Assert.Equal(1, result.RetainedReferencedHashes);
        IReadOnlyList<IndexedDbOperation> operations =
            Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(module.ExecuteArguments[1][4]);
        Assert.All(operations, operation =>
        {
            Assert.Equal("deleteAllByIndexIfUnreferenced", operation.Kind);
            Assert.Equal(KazoOsIndexedDbSchema.FileSystemEntryStoreName, operation.ReferenceObjectStoreName);
            Assert.Equal("contentHash", operation.ReferenceIndexName);
        });
    }

    [Fact]
    public async Task InitializeAsync_IgnoresRecentAndUndatedLegacyChunks()
    {
        long recentUtcMs = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        ScriptedModule module = new(One(
            $"[{{\"contentHash\":\"recent\",\"chunkIndex\":0,\"dataBase64\":\"\",\"createdUtcMs\":{recentUtcMs}}}," +
            "{\"contentHash\":\"legacy\",\"chunkIndex\":0,\"dataBase64\":\"\"}]"));
        await using IndexedDbFileSystemMaintenance maintenance = new(
            new FakeJsRuntime(module),
            FileContentStoragePolicy.Default,
            new FixedTimeProvider());

        IndexedDbFileContentCleanupResult result = await maintenance.InitializeAsync();

        Assert.Equal(new IndexedDbFileContentCleanupResult(0, 0, 0), result);
        Assert.Single(module.ExecuteArguments);
    }

    private static JsonElement[] One(string json) => [Parse(json)];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    private sealed class ScriptedModule(params JsonElement[][] results) : IJSObjectReference
    {
        private readonly Queue<JsonElement[]> _results = new(results);

        public List<object?[]> ExecuteArguments { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "executeTransaction")
            {
                ExecuteArguments.Add(args ?? []);
                return ValueTask.FromResult((TValue)(object)_results.Dequeue());
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    }
}