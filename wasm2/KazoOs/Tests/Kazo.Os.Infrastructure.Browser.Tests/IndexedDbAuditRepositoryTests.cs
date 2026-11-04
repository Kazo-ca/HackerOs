using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Diagnostics;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies durable append-only audit storage and pre-storage redaction.</summary>
public sealed class IndexedDbAuditRepositoryTests
{
    [Fact]
    public async Task Append_redacts_before_storage_and_read_preserves_primary_key_order()
    {
        FakeIndexedDbModule module = new();
        await using IndexedDbAuditRepository repository = new(
            new FakeJsRuntime(module),
            new TokenRedactor());

        await repository.AppendAsync(Entry("first", new Dictionary<string, string>
        {
            ["token"] = "raw-token"
        }));
        await repository.AppendAsync(Entry("second"));

        IReadOnlyList<AuditEntry> entries = await repository.ReadAllAsync();

        Assert.Equal(["first", "second"], entries.Select(entry => entry.Subject));
        Assert.DoesNotContain("raw-token", module.SerializedWrittenRecords, StringComparison.Ordinal);
        Assert.Contains("***redacted***", module.SerializedWrittenRecords, StringComparison.Ordinal);
        Assert.Equal(3, module.Invocations.Count);
        Assert.All(module.Invocations.Take(2), operations =>
        {
            IndexedDbOperation operation = Assert.Single(operations);
            Assert.Equal("add", operation.Kind);
            Assert.Equal(KazoOsIndexedDbSchema.AuditStoreName, operation.ObjectStoreName);
        });
    }

    private static AuditEntry Entry(
        string subject,
        IReadOnlyDictionary<string, string>? properties = null) => new(
            DateTimeOffset.FromUnixTimeMilliseconds(100),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "system",
            "test.write",
            subject,
            AuditOutcome.Success,
            properties);

    private sealed class TokenRedactor : IDiagnosticRedactor
    {
        public string Redact(string propertyKey, string value) =>
            StringComparer.OrdinalIgnoreCase.Equals(propertyKey, "token")
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

        internal List<IReadOnlyList<IndexedDbOperation>> Invocations { get; } = [];
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

            IReadOnlyList<IndexedDbOperation> operations =
                Assert.IsAssignableFrom<IReadOnlyList<IndexedDbOperation>>((args ?? [])[4]);
            Invocations.Add(operations);
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
                SerializedWrittenRecords += record.GetRawText();
                long id = _nextId++;
                _records.Add((id, record));
                return JsonSerializer.SerializeToElement(id);
            }

            Assert.Equal("getAll", operation.Kind);
            return JsonSerializer.SerializeToElement(_records
                .OrderBy(item => item.Record.GetProperty("timestampUtcMs").GetInt64())
                .ThenBy(item => item.Id)
                .Select(item => item.Record));
        }
    }
}