using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Diagnostics;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies durable diagnostic redaction, ordering, and bounded retention.</summary>
public sealed class IndexedDbDiagnosticRepositoryTests
{
    [Fact]
    public async Task Append_redacts_before_atomic_storage_and_evicts_the_oldest_entry()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbDiagnosticRepository repository = new(
            new FakeJsRuntime(module),
            new PasswordRedactor(),
            capacity: 2);

        await repository.AppendAsync(Entry(1, "first", new Dictionary<string, string>
        {
            ["password"] = "raw-secret"
        }));
        await repository.AppendAsync(Entry(2, "second"));
        await repository.AppendAsync(Entry(3, "third"));

        IReadOnlyList<DiagnosticEntry> retained = await repository.ReadAllAsync();

        Assert.Equal(["second", "third"], retained.Select(entry => entry.Message));
        Assert.DoesNotContain("raw-secret", module.SerializedWrittenRecords, StringComparison.Ordinal);
        Assert.Contains("***redacted***", module.SerializedWrittenRecords, StringComparison.Ordinal);
        Assert.Equal(4, module.TransactionInvocations.Count);
        Assert.All(module.TransactionInvocations.Take(3), invocation =>
        {
            Assert.Equal(IndexedDbTransactionMode.ReadWrite, invocation.Mode);
            Assert.Equal(["add", "trimOldest"], invocation.Operations.Select(operation => operation.Kind));
            Assert.Equal(2, invocation.Operations[1].Count);
        });
    }

    [Fact]
    public async Task Read_orders_equal_timestamps_by_auto_incremented_primary_key()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbDiagnosticRepository repository = new(
            new FakeJsRuntime(module),
            new PasswordRedactor(),
            capacity: 3);

        await repository.AppendAsync(Entry(1, "first"));
        await repository.AppendAsync(Entry(1, "second"));

        IReadOnlyList<DiagnosticEntry> retained = await repository.ReadAllAsync();

        Assert.Equal(["first", "second"], retained.Select(entry => entry.Message));
    }

    private static DiagnosticEntry Entry(
        long timestampUtcMs,
        string message,
        IReadOnlyDictionary<string, string>? properties = null) => new(
            DateTimeOffset.FromUnixTimeMilliseconds(timestampUtcMs),
            DiagnosticSeverity.Information,
            "storage-test",
            message,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            properties);

    private sealed class PasswordRedactor : IDiagnosticRedactor
    {
        public string Redact(string propertyKey, string value) =>
            StringComparer.OrdinalIgnoreCase.Equals(propertyKey, "password")
                ? "***redacted***"
                : value;
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
        private readonly List<(long Id, JsonElement Record)> _records = [];
        private long _nextId = 1;

        internal List<TransactionInvocation> TransactionInvocations { get; } = [];
        internal string SerializedWrittenRecords { get; private set; } = string.Empty;

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
            string modeValue = Assert.IsType<string>(arguments[3]);
            IReadOnlyList<IndexedDbOperation> operations =
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>(arguments[4]);
            IndexedDbTransactionMode mode = modeValue == "readwrite"
                ? IndexedDbTransactionMode.ReadWrite
                : IndexedDbTransactionMode.ReadOnly;
            TransactionInvocations.Add(new TransactionInvocation(mode, operations));

            JsonElement[] results = operations.Select(Execute).ToArray();
            return ValueTask.FromResult((TValue)(object)results);
        }

        private JsonElement Execute(IndexedDbOperation operation)
        {
            Assert.Equal(KazoOsIndexedDbSchema.DiagnosticsStoreName, operation.ObjectStoreName);
            if (operation.Kind == "add")
            {
                JsonElement record = JsonSerializer.SerializeToElement(
                    operation.Value,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                SerializedWrittenRecords += record.GetRawText();
                long id = _nextId++;
                _records.Add((id, record));
                return JsonSerializer.SerializeToElement(id);
            }

            if (operation.Kind == "trimOldest")
            {
                int removeCount = Math.Max(0, _records.Count - operation.Count!.Value);
                foreach ((long id, _) in Ordered().Take(removeCount).ToArray())
                {
                    _records.RemoveAll(candidate => candidate.Id == id);
                }

                return JsonSerializer.SerializeToElement(new { deleted = removeCount });
            }

            if (operation.Kind == "getAll")
            {
                return JsonSerializer.SerializeToElement(Ordered().Select(item => item.Record));
            }

            throw new InvalidOperationException($"Unexpected operation '{operation.Kind}'.");
        }

        private IOrderedEnumerable<(long Id, JsonElement Record)> Ordered() => _records
            .OrderBy(item => item.Record.GetProperty("timestampUtcMs").GetInt64())
            .ThenBy(item => item.Id);
    }

    private sealed record TransactionInvocation(
        IndexedDbTransactionMode Mode,
        IReadOnlyList<IndexedDbOperation> Operations);
}