using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;

namespace HackerOs.Platform.Core.Diagnostics;

/// <summary>
/// Bridges the standard <see cref="Microsoft.Extensions.Logging"/> pipeline into Kazo OS logging:
/// any class that takes an injected <c>ILogger&lt;T&gt;</c> writes one line per entry to
/// <c>/var/log/syslog</c> through <see cref="LogFileWriter"/> (ADR 0045), and keeps recording into
/// the in-memory <see cref="IDiagnosticSink"/> buffer of recent entries for live viewers.
/// </summary>
/// <remarks>
/// Every dependency is a singleton, so this provider can be built by the host's root
/// <c>ILoggerFactory</c> before any Blazor scope exists.
/// </remarks>
public sealed class HackerOsDiagnosticLoggerProvider : ILoggerProvider
{
    private readonly IDiagnosticSink _sink;
    private readonly LogFileWriter _writer;
    private readonly ISimulationClock _clock;
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Initializes a provider bound to the process-wide memory buffer and log writer.</summary>
    /// <param name="sink">In-memory buffer of recent entries.</param>
    /// <param name="writer">Writer appending to <c>/var/log/syslog</c>.</param>
    /// <param name="clock">Simulation clock stamping every entry.</param>
    /// <param name="redactor">Redactor applied to properties before they reach the file.</param>
    public HackerOsDiagnosticLoggerProvider(
        IDiagnosticSink sink,
        LogFileWriter writer,
        ISimulationClock clock,
        IDiagnosticRedactor? redactor = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _redactor = redactor ?? new SensitiveKeyDiagnosticRedactor();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new HackerOsDiagnosticLogger(_sink, _writer, _clock, _redactor, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// One category-scoped <see cref="ILogger"/>: records into <see cref="IDiagnosticSink"/> and queues
/// the same entry, redacted and formatted, for <c>syslog</c>. Never blocks the caller.
/// </summary>
internal sealed class HackerOsDiagnosticLogger : ILogger
{
    private readonly IDiagnosticSink _sink;
    private readonly LogFileWriter _writer;
    private readonly ISimulationClock _clock;
    private readonly IDiagnosticRedactor _redactor;
    private readonly string _category;

    public HackerOsDiagnosticLogger(
        IDiagnosticSink sink,
        LogFileWriter writer,
        ISimulationClock clock,
        IDiagnosticRedactor redactor,
        string category)
    {
        _sink = sink;
        _writer = writer;
        _clock = clock;
        _redactor = redactor;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        DiagnosticSeverity severity = ToSeverity(logLevel);
        string message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = exception?.Message ?? eventId.ToString();
        }

        Dictionary<string, string>? properties = null;
        if (exception is not null)
        {
            properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name
            };
        }

        DiagnosticEntry entry = new(_clock.UtcNow, severity, _category, message, Guid.NewGuid(), properties);
        _sink.Record(entry);
        _writer.Enqueue(LogFileTarget.Syslog, LogLineFormat.Format(DiagnosticRedaction.Apply(entry, _redactor)));
    }

    private static DiagnosticSeverity ToSeverity(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => DiagnosticSeverity.Trace,
        LogLevel.Debug => DiagnosticSeverity.Debug,
        LogLevel.Information => DiagnosticSeverity.Information,
        LogLevel.Warning => DiagnosticSeverity.Warning,
        LogLevel.Error => DiagnosticSeverity.Error,
        LogLevel.Critical => DiagnosticSeverity.Critical,
        _ => DiagnosticSeverity.Information
    };

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose()
        {
        }
    }
}
