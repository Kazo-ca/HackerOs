using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// <see cref="IAuditLog"/> backed by <c>/var/log/auth.log</c> (ADR 0045, section 5): every entry is
/// redacted, formatted with <see cref="LogLineFormat.FormatAudit"/>, and queued on
/// <see cref="LogFileWriter"/>. <see cref="Entries"/> stays served by an in-memory buffer of the most
/// recent <see cref="RecentEntryCapacity"/> entries; the file is the source of truth.
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    /// <summary>Number of recent entries kept in memory for <see cref="Entries"/>.</summary>
    public const int RecentEntryCapacity = 512;

    private readonly BoundedAuditLog _recent;
    private readonly LogFileWriter _writer;
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Initializes an audit log writing to <c>auth.log</c> through <paramref name="writer"/>.</summary>
    /// <param name="writer">The platform log writer.</param>
    /// <param name="redactor">Redactor applied to every property value before memory or file storage.</param>
    public FileAuditLog(LogFileWriter writer, IDiagnosticRedactor? redactor = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _redactor = redactor ?? new SensitiveKeyDiagnosticRedactor();
        _recent = new BoundedAuditLog(RecentEntryCapacity, _redactor);
    }

    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Redact before anything leaves this method: the file must never see a raw secret.
        AuditEntry redacted = Redact(entry);
        _recent.Record(redacted);
        _writer.Enqueue(LogFileTarget.AuthLog, LogLineFormat.FormatAudit(redacted));
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEntry> Entries => _recent.Entries;

    private AuditEntry Redact(AuditEntry entry)
    {
        if (entry.Properties.Count == 0)
        {
            return entry;
        }

        Dictionary<string, string> properties = entry.Properties.ToDictionary(
            pair => pair.Key,
            pair => _redactor.Redact(pair.Key, pair.Value),
            StringComparer.Ordinal);
        return new AuditEntry(
            entry.TimestampUtc,
            entry.CorrelationId,
            entry.Actor,
            entry.Action,
            entry.Subject,
            entry.Outcome,
            properties);
    }
}
