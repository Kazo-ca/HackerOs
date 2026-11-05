using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// In-memory <see cref="IAuditLog"/> that retains at most <see cref="MaxEntries"/> entries,
/// evicting the oldest entry once full, and redacts every property value before storage.
/// </summary>
public sealed class BoundedAuditLog : IAuditLog
{
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _entries = [];
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Initializes a bounded audit log.</summary>
    /// <param name="maxEntries">Maximum number of entries retained; must be at least one.</param>
    /// <param name="redactor">Redactor applied to every property value before storage.</param>
    public BoundedAuditLog(int maxEntries, IDiagnosticRedactor? redactor = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        MaxEntries = maxEntries;
        _redactor = redactor ?? new SensitiveKeyDiagnosticRedactor();
    }

    /// <summary>Gets the maximum number of entries retained.</summary>
    public int MaxEntries { get; }

    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        AuditEntry redacted = RedactionApplier.Apply(entry, _redactor);

        lock (_gate)
        {
            _entries.Add(redacted);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    private static class RedactionApplier
    {
        public static AuditEntry Apply(AuditEntry entry, IDiagnosticRedactor redactor)
        {
            if (entry.Properties.Count == 0)
            {
                return entry;
            }

            Dictionary<string, string> redactedProperties = entry.Properties.ToDictionary(
                pair => pair.Key,
                pair => redactor.Redact(pair.Key, pair.Value));

            return new AuditEntry(
                entry.TimestampUtc,
                entry.CorrelationId,
                entry.Actor,
                entry.Action,
                entry.Subject,
                entry.Outcome,
                redactedProperties);
        }
    }
}
