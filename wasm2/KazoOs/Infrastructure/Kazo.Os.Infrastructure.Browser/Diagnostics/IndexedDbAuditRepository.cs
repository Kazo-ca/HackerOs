using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Diagnostics;

/// <summary>Persists redacted append-only audit records in IndexedDB.</summary>
public sealed class IndexedDbAuditRepository : IPersistentAuditRepository, IAsyncDisposable
{
    private const string TransactionBoundary = "AuditAppend";
    private readonly IndexedDbInteropAdapter _adapter;
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Initializes a browser-backed audit repository.</summary>
    public IndexedDbAuditRepository(IJSRuntime runtime, IDiagnosticRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _adapter = new IndexedDbInteropAdapter(runtime);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        AuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditRecord record = AuditRecord.FromDomain(entry, _redactor);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation(
                "add",
                HackerOsIndexedDbSchema.AuditStoreName,
                Value: record)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AuditEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            TransactionBoundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "getAll",
                HackerOsIndexedDbSchema.AuditStoreName,
                IndexName: "timestampUtcMs")],
            cancellationToken).ConfigureAwait(false);

        JsonElement records = results.Single();
        if (records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted audit records must be returned as an array.");
        }

        return records.EnumerateArray().Select(AuditRecord.ToDomain).ToArray();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private ValueTask EnsureOpenAsync(CancellationToken cancellationToken) =>
        _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken);

    private sealed record AuditRecord(
        [property: JsonPropertyName("timestampUtcMs")] long TimestampUtcMs,
        [property: JsonPropertyName("correlationId")] string CorrelationId,
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("outcome")] int Outcome,
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, string> Properties)
    {
        internal static AuditRecord FromDomain(AuditEntry entry, IDiagnosticRedactor redactor) => new(
            entry.TimestampUtc.ToUnixTimeMilliseconds(),
            entry.CorrelationId.ToString("N"),
            entry.Actor,
            entry.Action,
            entry.Subject,
            (int)entry.Outcome,
            entry.Properties.ToDictionary(
                pair => pair.Key,
                pair => redactor.Redact(pair.Key, pair.Value),
                StringComparer.Ordinal));

        internal static AuditEntry ToDomain(JsonElement element)
        {
            int outcomeValue = element.GetProperty("outcome").GetInt32();
            if (!Enum.IsDefined((AuditOutcome)outcomeValue))
            {
                throw new InvalidDataException($"Persisted audit outcome '{outcomeValue}' is invalid.");
            }

            Dictionary<string, string> properties = element
                .GetProperty("properties")
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString()
                        ?? throw new InvalidDataException("An audit property value must be a string."),
                    StringComparer.Ordinal);

            return new AuditEntry(
                DateTimeOffset.FromUnixTimeMilliseconds(
                    element.GetProperty("timestampUtcMs").GetInt64()),
                Guid.ParseExact(element.GetProperty("correlationId").GetString()!, "N"),
                element.GetProperty("actor").GetString()
                    ?? throw new InvalidDataException("An audit actor is required."),
                element.GetProperty("action").GetString()
                    ?? throw new InvalidDataException("An audit action is required."),
                element.GetProperty("subject").GetString()
                    ?? throw new InvalidDataException("An audit subject is required."),
                (AuditOutcome)outcomeValue,
                properties);
        }
    }
}