using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Diagnostics;

/// <summary>Persists redacted diagnostics with bounded atomic retention in IndexedDB.</summary>
public sealed class IndexedDbDiagnosticRepository : IPersistentDiagnosticRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "DiagnosticsAppend";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly IDiagnosticRedactor _redactor;
    private readonly int _capacity;

    /// <summary>Initializes a browser-backed diagnostic repository.</summary>
    public IndexedDbDiagnosticRepository(
        IJSRuntime runtime,
        IDiagnosticRedactor redactor,
        int capacity)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        DiagnosticEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DiagnosticRecord record = DiagnosticRecord.FromDomain(entry, _redactor);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [
                new IndexedDbOperation(
                    "add",
                    KazoOsIndexedDbSchema.DiagnosticsStoreName,
                    Value: record),
                new IndexedDbOperation(
                    "trimOldest",
                    KazoOsIndexedDbSchema.DiagnosticsStoreName,
                    IndexName: "timestampUtcMs",
                    Count: _capacity)
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DiagnosticEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "getAll",
                KazoOsIndexedDbSchema.DiagnosticsStoreName,
                IndexName: "timestampUtcMs")],
            cancellationToken).ConfigureAwait(false);

        JsonElement records = results.Single();
        if (records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted diagnostics must be returned as an array.");
        }

        return records.EnumerateArray().Select(DiagnosticRecord.ToDomain).ToArray();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private sealed record DiagnosticRecord(
        [property: JsonPropertyName("timestampUtcMs")] long TimestampUtcMs,
        [property: JsonPropertyName("severity")] int Severity,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("correlationId")] string CorrelationId,
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, string> Properties)
    {
        internal static DiagnosticRecord FromDomain(
            DiagnosticEntry entry,
            IDiagnosticRedactor redactor) => new(
                entry.TimestampUtc.ToUnixTimeMilliseconds(),
                (int)entry.Severity,
                entry.Category,
                entry.Message,
                entry.CorrelationId.ToString("N"),
                entry.Properties.ToDictionary(
                    pair => pair.Key,
                    pair => redactor.Redact(pair.Key, pair.Value),
                    StringComparer.Ordinal));

        internal static DiagnosticEntry ToDomain(JsonElement element)
        {
            long timestampUtcMs = element.GetProperty("timestampUtcMs").GetInt64();
            int severityValue = element.GetProperty("severity").GetInt32();
            if (!Enum.IsDefined((DiagnosticSeverity)severityValue))
            {
                throw new InvalidDataException($"Persisted diagnostic severity '{severityValue}' is invalid.");
            }

            Dictionary<string, string> properties = element
                .GetProperty("properties")
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString()
                        ?? throw new InvalidDataException("A diagnostic property value must be a string."),
                    StringComparer.Ordinal);

            return new DiagnosticEntry(
                DateTimeOffset.FromUnixTimeMilliseconds(timestampUtcMs),
                (DiagnosticSeverity)severityValue,
                element.GetProperty("category").GetString()
                    ?? throw new InvalidDataException("A diagnostic category is required."),
                element.GetProperty("message").GetString()
                    ?? throw new InvalidDataException("A diagnostic message is required."),
                Guid.ParseExact(element.GetProperty("correlationId").GetString()!, "N"),
                properties);
        }
    }
}