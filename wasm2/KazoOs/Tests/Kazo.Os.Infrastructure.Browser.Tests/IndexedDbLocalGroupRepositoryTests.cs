using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Infrastructure.Browser.Sessions;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies local-group persistence through the typed C#/JS transaction boundary.</summary>
public sealed class IndexedDbLocalGroupRepositoryTests
{
    [Fact]
    public async Task Create_then_find_round_trips_the_group_through_one_store_boundary()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbLocalGroupRepository repository = new(new FakeJsRuntime(module));

        LocalGroup created = await repository.CreateGroupAsync(LocalLoginName.Parse("users"));
        LocalGroup? loaded = await repository.FindByIdAsync(created.Id);

        Assert.Equal(created, loaded);
        Assert.Equal(2, module.TransactionInvocations.Count);
        Assert.All(module.TransactionInvocations, invocation =>
        {
            Assert.Equal([KazoOsIndexedDbSchema.GroupStoreName], invocation.StoreNames);
            Assert.All(invocation.Operations, operation =>
                Assert.Equal(KazoOsIndexedDbSchema.GroupStoreName, operation.ObjectStoreName));
        });
    }

    [Fact]
    public async Task Find_returns_null_when_group_does_not_exist()
    {
        await using IndexedDbLocalGroupRepository repository = new(
            new FakeJsRuntime(new FakeIndexedDbModule()));

        LocalGroup? group = await repository.FindByIdAsync(LocalGroupId.FromGuid(Guid.NewGuid()));

        Assert.Null(group);
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

    private sealed class FakeIndexedDbModule : IJSObjectReference
    {
        private readonly Dictionary<string, JsonElement> _groups = new(StringComparer.Ordinal);

        internal List<TransactionInvocation> TransactionInvocations { get; } = [];

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
            IReadOnlyList<string> stores = Assert.IsAssignableFrom<IReadOnlyList<string>>(arguments[2]);
            IReadOnlyList<IndexedDbOperation> operations =
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(arguments[4]);
            TransactionInvocations.Add(new TransactionInvocation(stores, operations));

            JsonElement[] results = operations.Select(Execute).ToArray();
            return ValueTask.FromResult((TValue)(object)results);
        }

        private JsonElement Execute(IndexedDbOperation operation)
        {
            if (operation.Kind == "add")
            {
                JsonElement record = JsonSerializer.SerializeToElement(
                    operation.Value,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                _groups.Add(record.GetProperty("id").GetString()!, record);
                return JsonSerializer.SerializeToElement(record.GetProperty("id").GetString());
            }

            if (operation.Kind == "get" && _groups.TryGetValue((string)operation.Key!, out JsonElement value))
            {
                return value;
            }

            return JsonDocument.Parse("null").RootElement.Clone();
        }
    }

    private sealed record TransactionInvocation(
        IReadOnlyList<string> StoreNames,
        IReadOnlyList<IndexedDbOperation> Operations);
}
