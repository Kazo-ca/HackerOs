using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Records host failures with a user-safe correlation ID and redacted structured metadata, in the
/// in-memory buffer of recent entries and as a <c>CRIT</c> line in <c>/var/log/syslog</c> (ADR 0045).
/// </summary>
public sealed class HostExceptionReporter
{
    private const string Category = "ecosystem-host";

    private readonly IDiagnosticSink _sink;
    private readonly LogFileWriter _writer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the reporter over the memory buffer and the platform log writer.</summary>
    public HostExceptionReporter(
        IDiagnosticSink sink,
        LogFileWriter writer,
        TimeProvider timeProvider)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Records one failure without writing its potentially sensitive message or stack trace, then
    /// asks the writer to flush so the line survives a tab that is about to crash.
    /// </summary>
    public async ValueTask<Guid> ReportAsync(
        Exception exception,
        string phase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        Guid correlationId = Guid.NewGuid();
        DiagnosticEntry entry = new(
            _timeProvider.GetUtcNow(),
            DiagnosticSeverity.Critical,
            Category,
            "An unexpected host failure interrupted the current surface.",
            correlationId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["correlationId"] = correlationId.ToString("D"),
                ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["phase"] = phase
            });

        _sink.Record(entry);
        _writer.Enqueue(LogFileTarget.Syslog, LogLineFormat.Format(entry));
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception flushException) when (flushException is not OperationCanceledException)
        {
            // The writer already notes per-file failures in memory; this only guards the caller.
            _sink.Record(new DiagnosticEntry(
                _timeProvider.GetUtcNow(),
                DiagnosticSeverity.Warning,
                Category,
                "The host failure could not be written to the system log.",
                correlationId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = flushException.GetType().FullName ?? flushException.GetType().Name,
                    ["phase"] = "diagnostic-persistence"
                }));
        }

        return correlationId;
    }
}
