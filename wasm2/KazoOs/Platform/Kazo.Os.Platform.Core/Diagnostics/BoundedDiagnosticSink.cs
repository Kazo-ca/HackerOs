using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// In-memory <see cref="IDiagnosticSink"/> that retains at most <see cref="MaxEntries"/> entries,
/// evicting the oldest entry once full, and redacts every property value before storage.
/// </summary>
public sealed class BoundedDiagnosticSink : IDiagnosticSink
{
    private readonly Lock _gate = new();
    private readonly List<DiagnosticEntry> _entries = [];
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Initializes a bounded diagnostic sink.</summary>
    /// <param name="maxEntries">Maximum number of entries retained; must be at least one.</param>
    /// <param name="redactor">Redactor applied to every property value before storage.</param>
    public BoundedDiagnosticSink(int maxEntries, IDiagnosticRedactor? redactor = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        MaxEntries = maxEntries;
        _redactor = redactor ?? new SensitiveKeyDiagnosticRedactor();
    }

    /// <summary>Gets the maximum number of entries retained.</summary>
    public int MaxEntries { get; }

    /// <inheritdoc />
    public void Record(DiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        DiagnosticEntry redacted = RedactionApplier.Apply(entry, _redactor);

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
    public IReadOnlyList<DiagnosticEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static class RedactionApplier
    {
        public static DiagnosticEntry Apply(DiagnosticEntry entry, IDiagnosticRedactor redactor)
        {
            if (entry.Properties.Count == 0)
            {
                return entry;
            }

            Dictionary<string, string> redactedProperties = entry.Properties.ToDictionary(
                pair => pair.Key,
                pair => redactor.Redact(pair.Key, pair.Value));

            return new DiagnosticEntry(
                entry.TimestampUtc,
                entry.Severity,
                entry.Category,
                entry.Message,
                entry.CorrelationId,
                redactedProperties);
        }
    }
}
