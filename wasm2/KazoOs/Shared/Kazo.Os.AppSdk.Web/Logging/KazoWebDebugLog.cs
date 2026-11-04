using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.AppSdk.Web.Logging;

/// <summary>
/// The logging settings of a site (<see cref="Hosting.KazoWebApplicationBuilder.Logging"/>, ADR 0088 §5):
/// <c>ILogger&lt;T&gt;</c> is always available; nothing is written until the site enables its debug log.
/// </summary>
public sealed class KazoWebLoggingOptions
{
    /// <summary>Gets whether the site writes its debug log, <c>/var/log/hackweb/&lt;id&gt;.log</c>.</summary>
    public bool DebugLogEnabled { get; private set; }

    /// <summary>Gets the lowest level written (<see cref="LogLevel.Debug"/> by default).</summary>
    public LogLevel MinimumLevel { get; private set; } = LogLevel.Debug;

    /// <summary>
    /// Gets or sets whether the host writes a line when each request starts and ends, as ASP.NET Core's
    /// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> category does. On by default.
    /// </summary>
    public bool LogRequests { get; set; } = true;

    /// <summary>
    /// Turns on the debug log of the site: every <c>ILogger&lt;T&gt;</c> entry at <paramref name="minimumLevel"/>
    /// or above, the requests and the unhandled exceptions go to <c>/var/log/hackweb/&lt;app id&gt;.log</c>,
    /// written by the OS with the usual rotation (1 MiB, 5 archives).
    /// </summary>
    /// <param name="minimumLevel">The lowest level written.</param>
    public KazoWebLoggingOptions EnableDebugLog(LogLevel minimumLevel = LogLevel.Debug)
    {
        DebugLogEnabled = true;
        MinimumLevel = minimumLevel;
        return this;
    }
}

/// <summary>
/// Where the lines of a site's debug log go. The OS one (<see cref="KazoWebDebugLogGatewaySink"/>)
/// appends to <c>/var/log/hackweb/&lt;app id&gt;.log</c>; a test registers its own in the site's services.
/// </summary>
public interface IHackWebDebugLogSink
{
    /// <summary>Appends one line.</summary>
    /// <param name="line">The formatted line.</param>
    void Write(string line);
}

/// <summary>The OS sink: the logging gateway of the site's process (<c>AppendToSiteDebugLog</c>).</summary>
/// <param name="context">The site's execution context.</param>
public sealed class KazoWebDebugLogGatewaySink(IAppExecutionContext context) : IHackWebDebugLogSink
{
    /// <inheritdoc />
    public void Write(string line)
    {
        try
        {
            context.Logging.AppendToSiteDebugLog(line);
        }
        catch (UnauthorizedAccessException)
        {
            // A host without site logs: a debug log must never break the site.
        }
    }
}

/// <summary>A sink that keeps the lines in memory (tests, or a site run without the OS).</summary>
public sealed class KazoWebDebugLogMemorySink : IHackWebDebugLogSink
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    /// <summary>Gets a copy of the lines written so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <inheritdoc />
    public void Write(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }
}

/// <summary>
/// The <see cref="ILoggerProvider"/> of a site's debug log. Lines look like ASP.NET Core's console logger
/// in single-line mode, with the time first: <c>2026-10-17T01:22:14.123Z info: Category[0] message</c>;
/// an exception follows on the same line (the log writer escapes its line breaks).
/// </summary>
internal sealed class KazoWebDebugLogProvider(IHackWebDebugLogSink sink, TimeProvider time) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(sink, time, categoryName);

    public void Dispose()
    {
    }

    /// <summary>The short level names of ASP.NET Core's console logger.</summary>
    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    private sealed class Logger(IHackWebDebugLogSink sink, TimeProvider time, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            StringBuilder line = new();
            line.Append(time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                .Append(' ').Append(LevelName(logLevel)).Append(": ")
                .Append(category).Append('[').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append("] ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                line.Append(' ').Append(exception);
            }

            sink.Write(line.ToString());
        }
    }
}
