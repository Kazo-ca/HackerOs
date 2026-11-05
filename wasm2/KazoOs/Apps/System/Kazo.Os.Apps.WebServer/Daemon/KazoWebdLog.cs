using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;

namespace Kazo.Os.Apps.WebServer.Daemon;

/// <summary>
/// The log files of <c>kazowebd</c> (ADR 0077, section 2): one access log per site,
/// <c>/var/log/kazoweb/&lt;site&gt;.access.log</c>, in Apache/nginx "combined" format, and one
/// <c>/var/log/kazoweb/error.log</c> in nginx's error-log format. The platform's log writer does the
/// writes (ADR 0080, section 3), with rotation at 1 MiB like every other log.
/// </summary>
public static class KazoWebdLog
{
    /// <summary>The log directory, under <c>/var/log</c>.</summary>
    public const string DirectoryName = "kazoweb";

    /// <summary>Absolute path of the log directory.</summary>
    public const string Directory = "/var/log/" + DirectoryName;

    /// <summary>Absolute path of the error log.</summary>
    public const string ErrorLogPath = Directory + "/error.log";

    /// <summary>The server's name and version, sent as <c>Server</c> and printed on error pages.</summary>
    public const string ServerSignature = "kazowebd/1.0";

    /// <summary>Gets the absolute path of the access log of <paramref name="site"/>.</summary>
    /// <param name="site">A valid site name.</param>
    public static string AccessLogPath(string site) => $"{Directory}/{site}.access.log";

    /// <summary>
    /// Formats one access-log line in the "combined" format:
    /// <c>127.0.0.1 - - [16/Oct/2026:12:00:00 +0000] "GET / HTTP/1.1" 200 512 "-" "curl/8.5.0"</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="status">The status code sent.</param>
    /// <param name="bytes">Body bytes sent (<c>-</c> when zero, as Apache prints).</param>
    /// <param name="time">Simulated time of the request.</param>
    public static string FormatAccess(KazoHttpListenerRequest request, int status, long bytes, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(request);
        string when = time.UtcDateTime.ToString("dd/MMM/yyyy:HH:mm:ss", CultureInfo.InvariantCulture) + " +0000";
        string size = bytes > 0 ? bytes.ToString(CultureInfo.InvariantCulture) : "-";
        return $"{request.RemoteEndPoint.Address} - - [{when}] \"{request.HttpMethod} {request.RawUrl} HTTP/1.1\" {status} {size} " +
            $"\"{Quote(request.Headers["Referer"])}\" \"{Quote(request.UserAgent)}\"";
    }

    /// <summary>
    /// Formats one error-log line: <c>2026/10/16 12:00:00 [error] 42#0: docs: message</c>.
    /// </summary>
    /// <param name="level">nginx level: <c>error</c>, <c>warn</c>, <c>notice</c>.</param>
    /// <param name="site">The site concerned, or <see langword="null"/> for the server itself.</param>
    /// <param name="message">The message.</param>
    /// <param name="time">Simulated time.</param>
    /// <param name="pid">PID of <c>kazowebd</c>.</param>
    public static string FormatError(string level, string? site, string message, DateTimeOffset time, long pid)
    {
        string when = time.UtcDateTime.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        return site is null
            ? $"{when} [{level}] {pid}#0: {message}"
            : $"{when} [{level}] {pid}#0: {site}: {message}";
    }

    /// <summary>Appends an access line for <paramref name="site"/>.</summary>
    internal static void WriteAccess(IAppExecutionContext context, string site, string line) =>
        context.Logging.AppendToLogFile($"{DirectoryName}/{site}.access.log", line);

    /// <summary>Appends an error-log line.</summary>
    internal static void WriteError(IAppExecutionContext context, string level, string? site, string message) =>
        context.Logging.AppendToLogFile(
            $"{DirectoryName}/error.log",
            FormatError(level, site, message, context.Clock.UtcNow, context.ProcessId.Value));

    private static string Quote(string? value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
