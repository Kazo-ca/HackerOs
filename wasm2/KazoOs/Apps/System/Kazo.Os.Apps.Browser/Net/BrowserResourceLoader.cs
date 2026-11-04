using Kazo.Os.Apps.Browser.DevTools;

namespace Kazo.Os.Apps.Browser.Net;

/// <summary>
/// Sends the requests of one tab and records each one in the developer tools, as Chromium's
/// network stack feeds its Network panel: method, status, type, size and duration, plus the
/// "Failed to load resource" console line of a failed sub-resource (ADR 0075 §2).
/// </summary>
public sealed class BrowserResourceLoader
{
    private readonly IBrowserTransport _transport;
    private readonly TimeProvider _time;

    /// <summary>Initializes a loader.</summary>
    /// <param name="transport">How requests reach the network or the file system.</param>
    /// <param name="log">The tab's developer tools log.</param>
    /// <param name="time">Clock used for durations; the system clock by default.</param>
    public BrowserResourceLoader(IBrowserTransport transport, DevToolsLog log, TimeProvider? time = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the developer tools log the loader writes to.</summary>
    public DevToolsLog Log { get; }

    /// <summary>Sends a request and logs it.</summary>
    /// <param name="request">The request.</param>
    /// <param name="kind">What asked for it, the Network panel's "Type" column.</param>
    /// <param name="cancellationToken">Cancels the request (a new navigation does).</param>
    public async Task<BrowserFetchResult> SendAsync(BrowserRequest request, NetworkRequestKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started = _time.GetTimestamp();
        BrowserFetchResult result = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        TimeSpan duration = _time.GetElapsedTime(started);

        string url = BrowserUrl.ToDisplayString(request.Url);
        if (result.Response is { } response)
        {
            Log.Add(new NetworkLogEntry(request.Method, url, kind, response.StatusCode, null, response.MediaType, response.Body.Length, duration));
            if (response.StatusCode >= 400)
            {
                Log.Add(new ConsoleLogEntry(
                    ConsoleLevel.Error,
                    $"Failed to load resource: the server responded with a status of {response.StatusCode} ({response.ReasonPhrase})",
                    url));
            }
        }
        else
        {
            Log.Add(new NetworkLogEntry(request.Method, url, kind, 0, result.ErrorCode, string.Empty, 0, duration));
            Log.Add(new ConsoleLogEntry(ConsoleLevel.Error, $"Failed to load resource: net::{result.ErrorCode}", url));
        }

        return result;
    }

    /// <summary>
    /// Records a request the browser refused to send (a web page asking for a <c>file://</c> URL,
    /// a CORS failure…), with the console message Chromium prints for it.
    /// </summary>
    public void LogBlocked(BrowserRequest request, NetworkRequestKind kind, string errorCode, string consoleMessage)
    {
        ArgumentNullException.ThrowIfNull(request);
        string url = BrowserUrl.ToDisplayString(request.Url);
        Log.Add(new NetworkLogEntry(request.Method, url, kind, 0, errorCode, string.Empty, 0, TimeSpan.Zero));
        Log.Add(new ConsoleLogEntry(ConsoleLevel.Error, consoleMessage, url));
    }
}
