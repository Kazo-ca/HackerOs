using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies path resolution and deterministic directory reads at the browser boundary.</summary>
public sealed class IndexedDbFileSystemReaderTests
{
    private const string RootId = "00000000000000000000000000000001";
    private const string HomeId = "11111111111111111111111111111111";
    private const string UserId = "22222222222222222222222222222222";

    [Fact]
    public async Task ResolveAsync_RootReadsStableRootEntryOnly()
    {
        ScriptedJsObjectReference module = new(One(Entry(RootId, FileSystemEntryKind.Directory)));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        IndexedDbFileSystemEntryRecord? result = await reader.ResolveAsync(VirtualPath.Parse("/"));

        Assert.Equal(RootId, result?.Id);
        Assert.Single(module.ExecuteInvocations);
        Assert.Equal(RootId, Operation(module.ExecuteInvocations[0]).Key);
    }

    [Fact]
    public async Task ResolveAsync_WalksCompoundPathInOneTransaction()
    {
        ScriptedJsObjectReference module = new(
            One($"[{Entry(HomeId, FileSystemEntryKind.Directory)},{Entry(UserId, FileSystemEntryKind.Directory)}]"));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        IndexedDbFileSystemEntryRecord? result = await reader.ResolveAsync(VirtualPath.Parse("/home/user"));

        Assert.Equal(UserId, result?.Id);
        IndexedDbOperation walk = Operation(Assert.Single(module.ExecuteInvocations));
        Assert.Equal("resolvePath", walk.Kind);
        Assert.Equal("fsEntries", walk.ObjectStoreName);
        Assert.Equal("fsLinks", walk.ReferenceObjectStoreName);
        Assert.Equal(RootId, walk.Key);
        Assert.Equal(["home", "user"], Assert.IsType<string[]>(walk.Query));
    }

    [Fact]
    public async Task ResolveAsync_MissingComponentResolvesToNull()
    {
        // Only "/home" exists: the walk stops before the missing name.
        ScriptedJsObjectReference module = new(One($"[{Entry(HomeId, FileSystemEntryKind.Directory)}]"));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        IndexedDbFileSystemEntryRecord? result = await reader.ResolveAsync(VirtualPath.Parse("/home/missing"));

        Assert.Null(result);
        Assert.Single(module.ExecuteInvocations);
    }

    [Fact]
    public async Task ResolveComponentsAsync_ReturnsEachExistingComponentInOrder()
    {
        ScriptedJsObjectReference module = new(One($"[{Entry(HomeId, FileSystemEntryKind.Directory)}]"));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        IReadOnlyList<IndexedDbFileSystemEntryRecord> components =
            await reader.ResolveComponentsAsync(VirtualPath.Parse("/home/missing/deeper"));

        Assert.Equal([HomeId], components.Select(component => component.Id));
        Assert.Equal(["home", "missing", "deeper"], Assert.IsType<string[]>(Operation(Assert.Single(module.ExecuteInvocations)).Query));
    }

    [Fact]
    public async Task ResolveAsync_LinkToMissingEntryReportsCorruption()
    {
        ScriptedJsObjectReference module = new(One($$"""[{"danglingLink":"home","entryId":"{{HomeId}}"}]"""));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ResolveAsync(VirtualPath.Parse("/home/user")));

        Assert.Contains("'home'", error.Message, StringComparison.Ordinal);
        Assert.Contains(HomeId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnumerateAsync_SortsLinksAndBatchesEntryReads()
    {
        ScriptedJsObjectReference module = new(
            One(Entry(RootId, FileSystemEntryKind.Directory, revision: 7)),
            One($"[{LinkJson(RootId, "zeta", UserId)},{LinkJson(RootId, "Alpha", HomeId)}]"),
            [Parse(Entry(HomeId, FileSystemEntryKind.File)), Parse(Entry(UserId, FileSystemEntryKind.Directory))]);
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        IReadOnlyList<(FileSystemEntryName Name, IndexedDbFileSystemEntryRecord Entry)>? children =
            await reader.EnumerateAsync(VirtualPath.Parse("/"));

        IReadOnlyList<(FileSystemEntryName Name, IndexedDbFileSystemEntryRecord Entry)> existing =
            Assert.IsAssignableFrom<IReadOnlyList<(FileSystemEntryName, IndexedDbFileSystemEntryRecord)>>(children);
        Assert.Equal(["Alpha", "zeta"], existing.Select(child => child.Name.Value));
        Assert.Equal([HomeId, UserId], existing.Select(child => child.Entry.Id));
        JsInvocation linkQuery = module.ExecuteInvocations[1];
        IndexedDbOperation query = Operation(linkQuery);
        Assert.Equal("parentId", query.IndexName);
        Assert.Equal(RootId, query.Query);
        Assert.Equal(2, Operations(module.ExecuteInvocations[2]).Count);
    }

    [Fact]
    public async Task EnumerateAsync_LinkToMissingEntryReportsCorruption()
    {
        ScriptedJsObjectReference module = new(
            One(Entry(RootId, FileSystemEntryKind.Directory)),
            One($"[{LinkJson(RootId, "lost", HomeId)}]"),
            One("null"));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.EnumerateAsync(VirtualPath.Parse("/")));

        Assert.Contains(HomeId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MeasureStoredContentBytesAsync_CountsEachDistinctContentOnce()
    {
        // Two files share one blob (stored once), one has its own, directories have none.
        ScriptedJsObjectReference module = new(One("""
            [
              {"id":"a","kind":0,"length":1000,"contentHash":"h1"},
              {"id":"b","kind":0,"length":1000,"contentHash":"h1"},
              {"id":"c","kind":0,"length":24,"contentHash":"h2"},
              {"id":"d","kind":1,"length":0,"contentHash":null}
            ]
            """));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module));

        long used = await reader.MeasureStoredContentBytesAsync();

        Assert.Equal(1024, used);
        IndexedDbOperation scan = Operation(module.ExecuteInvocations[0]);
        Assert.Equal("getAll", scan.Kind);
    }

    [Fact]
    public async Task Transactions_are_reported_to_the_disk_statistics_as_4KiB_reads()
    {
        RecordingDisk disk = new();
        ScriptedJsObjectReference module = new(
            One(Entry(RootId, FileSystemEntryKind.Directory)),
            One("[]"));
        await using IndexedDbFileSystemReader reader = new(new FakeJsRuntime(module), disk);

        await reader.ResolveAsync(VirtualPath.Parse("/"));
        await reader.ResolveAsync(VirtualPath.Parse("/missing/child"));

        // One request per transaction, however deep the walk: a path lookup is one metadata read.
        Assert.Equal(2, disk.Begun);
        Assert.Equal(
            [(BlockIoDirection.Read, 4096L, true), (BlockIoDirection.Read, 4096L, true)],
            disk.Completed);
    }

    private static IndexedDbOperation Operation(JsInvocation invocation) => Assert.Single(Operations(invocation));

    private static IReadOnlyList<IndexedDbOperation> Operations(JsInvocation invocation) =>
        Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(invocation.Arguments[4]);

    private static JsonElement[] One(string json) => [Parse(json)];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Link(string parentId, string name, string entryId) => LinkJson(parentId, name, entryId);

    private static string LinkJson(string parentId, string name, string entryId) =>
        $"{{\"parentId\":\"{parentId}\",\"name\":\"{name}\",\"entryId\":\"{entryId}\"}}";

    private static string Entry(string id, FileSystemEntryKind kind, long revision = 1) => $$"""
        {
          "id":"{{id}}",
          "kind":{{(int)kind}},
          "ownerId":"system",
          "groupId":"system",
          "permissionsMode":493,
          "createdUtcMs":1785664800000,
          "contentModifiedUtcMs":1785664800000,
          "metadataChangedUtcMs":1785664800000,
          "revision":{{revision}},
          "length":0,
          "symbolicLinkTarget":null,
          "contentHash":null,
          "contentKind":1,
          "mediaType":"application/octet-stream",
          "encodingName":null
        }
        """;

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult((TValue)module);
    }

    private sealed class ScriptedJsObjectReference(params JsonElement[][] transactionResults) : IJSObjectReference
    {
        private readonly Queue<JsonElement[]> _results = new(transactionResults);

        public List<JsInvocation> ExecuteInvocations { get; } = [];

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
                ExecuteInvocations.Add(new JsInvocation(identifier, args ?? []));
                return ValueTask.FromResult((TValue)(object)_results.Dequeue());
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed record JsInvocation(string Identifier, object?[] Arguments);

    private sealed class RecordingDisk : IBlockDeviceStatistics
    {
        public int Begun { get; private set; }

        public List<(BlockIoDirection Direction, long Bytes, bool Succeeded)> Completed { get; } = [];

        public string DeviceName => "hda";

        public int Major => 8;

        public int Minor => 0;

        public void Begin() => Begun++;

        public void Complete(BlockIoDirection direction, long bytes, TimeSpan elapsed, bool succeeded) =>
            Completed.Add((direction, bytes, succeeded));

        public BlockDeviceCounters GetCounters() => default;
    }
}
