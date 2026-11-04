namespace Kazo.Os.Simulation.Abstractions.Diagnostics;

/// <summary>Defines the ordered severity of one structured diagnostic log entry.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Fine-grained internal tracing, normally disabled.</summary>
    Trace = 0,

    /// <summary>Developer-facing diagnostic detail.</summary>
    Debug = 1,

    /// <summary>Routine, expected operational information.</summary>
    Information = 2,

    /// <summary>An unexpected but recoverable condition.</summary>
    Warning = 3,

    /// <summary>An operation failed.</summary>
    Error = 4,

    /// <summary>A failure that threatens overall platform stability.</summary>
    Critical = 5
}

/// <summary>Represents one structured diagnostic log entry.</summary>
public sealed record DiagnosticEntry
{
    /// <summary>Initializes a validated diagnostic entry.</summary>
    /// <param name="timestampUtc">UTC simulation time the entry was recorded.</param>
    /// <param name="severity">Severity of the entry.</param>
    /// <param name="category">Non-empty logical source of the entry, e.g. a subsystem name.</param>
    /// <param name="message">Non-empty human-readable message.</param>
    /// <param name="correlationId">Identifier correlating this entry with related work.</param>
    /// <param name="properties">Optional structured properties; values are redacted by the sink before storage.</param>
    /// <exception cref="ArgumentException">Category or message is empty.</exception>
    public DiagnosticEntry(
        DateTimeOffset timestampUtc,
        DiagnosticSeverity severity,
        string category,
        string message,
        Guid correlationId,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("A category is required.", nameof(category));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A message is required.", nameof(message));
        }

        TimestampUtc = timestampUtc;
        Severity = severity;
        Category = category;
        Message = message;
        CorrelationId = correlationId;
        Properties = properties ?? new Dictionary<string, string>();
    }

    /// <summary>Gets the UTC simulation time the entry was recorded.</summary>
    public DateTimeOffset TimestampUtc { get; }

    /// <summary>Gets the severity of the entry.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Gets the logical source of the entry, e.g. a subsystem name.</summary>
    public string Category { get; }

    /// <summary>Gets the human-readable message.</summary>
    public string Message { get; }

    /// <summary>Gets the identifier correlating this entry with related work.</summary>
    public Guid CorrelationId { get; }

    /// <summary>Gets optional structured properties, already redacted.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; }
}

/// <summary>Records structured diagnostic entries with bounded, most-recent-first retention.</summary>
public interface IDiagnosticSink
{
    /// <summary>Records one diagnostic entry, evicting the oldest entry if retention is full.</summary>
    /// <param name="entry">The entry to record.</param>
    void Record(DiagnosticEntry entry);

    /// <summary>Gets every retained entry, oldest first.</summary>
    IReadOnlyList<DiagnosticEntry> Entries { get; }

    /// <summary>Discards every retained entry.</summary>
    void Clear();
}

/// <summary>Published each time a <see cref="DiagnosticEntry"/> is recorded, for live subscribers.</summary>
public sealed record DiagnosticEntryRecordedEvent(DiagnosticEntry Entry);

/// <summary>Published each time the diagnostic log is cleared, for live subscribers.</summary>
public sealed record DiagnosticLogClearedEvent(DateTimeOffset AtUtc);

/// <summary>Persists structured diagnostic entries through an asynchronous durable boundary.</summary>
public interface IPersistentDiagnosticRepository
{
    /// <summary>Commits one redacted entry and applies configured retention atomically.</summary>
    ValueTask AppendAsync(
        DiagnosticEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>Loads retained entries ordered from oldest to newest.</summary>
    ValueTask<IReadOnlyList<DiagnosticEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Defines the outcome of one audited operation.</summary>
public enum AuditOutcome
{
    /// <summary>The operation was authorized and completed.</summary>
    Success,

    /// <summary>The operation was denied by policy.</summary>
    Denied,

    /// <summary>The operation was authorized but failed to complete.</summary>
    Failed
}

/// <summary>Represents one immutable audit record for a security- or policy-relevant operation.</summary>
public sealed record AuditEntry
{
    /// <summary>Initializes a validated audit entry.</summary>
    /// <param name="timestampUtc">UTC simulation time the operation was audited.</param>
    /// <param name="correlationId">Identifier correlating this entry with related diagnostic entries.</param>
    /// <param name="actor">Non-empty identifier of the principal or system component performing the operation.</param>
    /// <param name="action">Non-empty, stable action identifier, e.g. <c>capability.grant</c>.</param>
    /// <param name="subject">Non-empty identifier of the resource the operation targeted.</param>
    /// <param name="outcome">Outcome of the operation.</param>
    /// <param name="properties">Optional structured properties; values are redacted by the audit log before storage.</param>
    /// <exception cref="ArgumentException">Actor, action, or subject is empty.</exception>
    public AuditEntry(
        DateTimeOffset timestampUtc,
        Guid correlationId,
        string actor,
        string action,
        string subject,
        AuditOutcome outcome,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("An actor is required.", nameof(actor));
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("An action is required.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("A subject is required.", nameof(subject));
        }

        TimestampUtc = timestampUtc;
        CorrelationId = correlationId;
        Actor = actor;
        Action = action;
        Subject = subject;
        Outcome = outcome;
        Properties = properties ?? new Dictionary<string, string>();
    }

    /// <summary>Gets the UTC simulation time the operation was audited.</summary>
    public DateTimeOffset TimestampUtc { get; }

    /// <summary>Gets the identifier correlating this entry with related diagnostic entries.</summary>
    public Guid CorrelationId { get; }

    /// <summary>Gets the identifier of the principal or system component performing the operation.</summary>
    public string Actor { get; }

    /// <summary>Gets the stable action identifier, e.g. <c>capability.grant</c>.</summary>
    public string Action { get; }

    /// <summary>Gets the identifier of the resource the operation targeted.</summary>
    public string Subject { get; }

    /// <summary>Gets the outcome of the operation.</summary>
    public AuditOutcome Outcome { get; }

    /// <summary>Gets optional structured properties, already redacted.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; }
}

/// <summary>Records audit entries with bounded, most-recent-first retention.</summary>
public interface IAuditLog
{
    /// <summary>Records one audit entry, evicting the oldest entry if retention is full.</summary>
    /// <param name="entry">The entry to record.</param>
    void Record(AuditEntry entry);

    /// <summary>Gets every retained entry, oldest first.</summary>
    IReadOnlyList<AuditEntry> Entries { get; }
}

/// <summary>Persists immutable audit entries through an asynchronous durable boundary.</summary>
public interface IPersistentAuditRepository
{
    /// <summary>Commits one redacted audit entry.</summary>
    ValueTask AppendAsync(
        AuditEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>Loads audit entries ordered from oldest to newest.</summary>
    ValueTask<IReadOnlyList<AuditEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Redacts sensitive structured property values before they are stored or displayed.</summary>
public interface IDiagnosticRedactor
{
    /// <summary>
    /// Returns the value to store for one structured property, redacting it if the key is
    /// considered sensitive.
    /// </summary>
    /// <param name="propertyKey">Structured property key.</param>
    /// <param name="value">Untrusted property value.</param>
    /// <returns>The original value, or a redaction placeholder if the key is sensitive.</returns>
    string Redact(string propertyKey, string value);
}
