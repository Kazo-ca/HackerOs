using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Provides one app instance's structured diagnostic logging, stamping every entry with the
/// app's own ID as source, its PID, and a fresh per-call correlation ID. Entries go to
/// <c>/var/log/apps/&lt;appId&gt;.log</c> through <see cref="LogFileWriter"/> (ADR 0045) and to the
/// in-memory buffer of recent entries. The app itself never writes under <c>/var/log</c>.
/// </summary>
public sealed class AppLoggingGateway : IAppLoggingGateway
{
    private readonly IDiagnosticSink _sink;
    private readonly ISimulationClock _clock;
    private readonly string _appId;
    private readonly LogFileWriter? _writer;
    private readonly LogFileTarget? _target;
    private readonly long? _processId;
    private readonly IDiagnosticRedactor _redactor;
    private readonly bool _isSystemApp;
    private readonly bool _isWebApp;

    /// <summary>Initializes a logging gateway bound to one app.</summary>
    /// <param name="sink">In-memory buffer of recent entries.</param>
    /// <param name="clock">Simulation clock stamping every entry.</param>
    /// <param name="appId">App identifier, used as source and as log file name.</param>
    /// <param name="writer">
    /// Platform log writer, or <see langword="null"/> to keep entries in memory only (headless tests).
    /// </param>
    /// <param name="processId">PID written in each line, when the hosting process is known.</param>
    /// <param name="redactor">Redactor applied to properties before they reach the file.</param>
    /// <param name="isSystemApp">
    /// Whether the app is a system app (ADR 0048), which may keep daemon logs of its own
    /// (<see cref="AppendToLogFile"/>, ADR 0080).
    /// </param>
    /// <param name="isWebApp">
    /// Whether the app is a web site (<c>AppKind.Web</c>), which may keep a debug log
    /// (<see cref="AppendToSiteDebugLog"/>, ADR 0088).
    /// </param>
    public AppLoggingGateway(
        IDiagnosticSink sink,
        ISimulationClock clock,
        string appId,
        LogFileWriter? writer = null,
        long? processId = null,
        IDiagnosticRedactor? redactor = null,
        bool isSystemApp = false,
        bool isWebApp = false)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _appId = appId;
        _writer = writer;
        _target = writer is null ? null : LogFileTarget.ForApp(appId);
        _processId = processId;
        _redactor = redactor ?? new SensitiveKeyDiagnosticRedactor();
        _isSystemApp = isSystemApp;
        _isWebApp = isWebApp;
    }

    /// <inheritdoc />
    public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null)
    {
        DiagnosticEntry entry = new(_clock.UtcNow, severity, _appId, message, Guid.NewGuid(), properties);
        _sink.Record(entry);
        if (_writer is not null)
        {
            _writer.Enqueue(_target!, LogLineFormat.Format(DiagnosticRedaction.Apply(entry, _redactor), _processId));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The line goes through <see cref="LogFileWriter"/> like every other log line: batched, rotated at
    /// 1 MiB, written as <c>system:system 0644</c>. Without a writer (headless tests) it is kept in the
    /// in-memory buffer with the file as its category, so tests can still read it back.
    /// </remarks>
    public void AppendToLogFile(string relativePath, string line)
    {
        if (!_isSystemApp)
        {
            throw new UnauthorizedAccessException($"Only a system app may write a daemon log under /var/log ('{_appId}' is not one).");
        }

        LogFileTarget target = LogFileTarget.ForDaemon(relativePath);
        ArgumentNullException.ThrowIfNull(line);
        // One entry is one line: a raw line break would forge a second entry.
        string escaped = line.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (_writer is not null)
        {
            _writer.Enqueue(target, escaped);
            return;
        }

        _sink.Record(new DiagnosticEntry(_clock.UtcNow, DiagnosticSeverity.Information, target.Path.Value, escaped, Guid.NewGuid(), null));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>/var/log/kazoweb/&lt;app id&gt;.log</c>, through <see cref="LogFileWriter"/> like
    /// <see cref="AppendToLogFile"/>; without a writer the line stays in the in-memory buffer.
    /// </remarks>
    public void AppendToSiteDebugLog(string line)
    {
        if (!_isWebApp)
        {
            throw new UnauthorizedAccessException($"Only a web app keeps a site debug log ('{_appId}' is not one).");
        }

        ArgumentNullException.ThrowIfNull(line);
        LogFileTarget target = LogFileTarget.ForDaemon($"kazoweb/{_appId}.log");
        string escaped = line.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (_writer is not null)
        {
            _writer.Enqueue(target, escaped);
            return;
        }

        _sink.Record(new DiagnosticEntry(_clock.UtcNow, DiagnosticSeverity.Debug, target.Path.Value, escaped, Guid.NewGuid(), null));
    }
}
