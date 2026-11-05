using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies clean-profile root initialization at the browser boundary.</summary>
public sealed class IndexedDbFileSystemBootstrapperTests
{
    [Fact]
    public async Task EnsureRootAsync_UsesStableAtomicAddIfAbsent()
    {
        FakeJsObjectReference module = new();
        TimeProvider time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        await using IndexedDbFileSystemBootstrapper bootstrapper = new(new FakeJsRuntime(module), time);

        await bootstrapper.EnsureRootAsync();

        Assert.Equal("openDatabase", module.Invocations[0].Identifier);
        JsInvocation execute = module.Invocations[1];
        IndexedDbOperation operation = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(execute.Arguments[4]));
        Assert.Equal("addIfAbsent", operation.Kind);
        Assert.Equal(IndexedDbFileSystemBootstrapper.RootEntryId.ToString(), operation.Key);
        IndexedDbFileSystemEntryRecord root = Assert.IsType<IndexedDbFileSystemEntryRecord>(operation.Value);
        Assert.Equal(1, root.Revision);
        Assert.Equal(0x01ED, root.PermissionsMode);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), root.CreatedUtcMs);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
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

    private sealed class FakeJsObjectReference : IJSObjectReference
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
                JsonElement added = JsonDocument.Parse("{\"added\":false}").RootElement.Clone();
                return ValueTask.FromResult((TValue)(object)new[] { added });
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed record JsInvocation(string Identifier, object?[] Arguments);
}
