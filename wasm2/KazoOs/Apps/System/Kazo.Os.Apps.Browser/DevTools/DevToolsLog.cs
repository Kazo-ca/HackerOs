namespace Kazo.Os.Apps.Browser.DevTools;

/// <summary>What started a request, the "Initiator"/"Type" column of the Network panel.</summary>
public enum NetworkRequestKind
{
    /// <summary>The page itself.</summary>
    Document,

    /// <summary>A style sheet (<c>&lt;link rel=stylesheet&gt;</c>, <c>@import</c>).</summary>
    Stylesheet,

    /// <summary>A script (<c>&lt;script src&gt;</c>).</summary>
    Script,

    /// <summary>An image, including CSS backgrounds and icons.</summary>
    Image,

    /// <summary>A font (<c>@font-face</c>).</summary>
    Font,

    /// <summary>Audio or video.</summary>
    Media,

    /// <summary>A <c>fetch()</c> call of the page.</summary>
    Fetch,

    /// <summary>An <c>XMLHttpRequest</c> of the page.</summary>
    Xhr,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>One row of the Network panel.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Url">Requested URL.</param>
/// <param name="Kind">What asked for it.</param>
/// <param name="StatusCode">Status code, or <c>0</c> when the request failed.</param>
/// <param name="Error">The <c>ERR_*</c> code or blocking reason of a failed request, else <see langword="null"/>.</param>
/// <param name="MediaType">Response <c>type/subtype</c>.</param>
/// <param name="Size">Response body size in bytes.</param>
/// <param name="Duration">Time from request to response.</param>
public sealed record NetworkLogEntry(
    string Method,
    string Url,
    NetworkRequestKind Kind,
    int StatusCode,
    string? Error,
    string MediaType,
    int Size,
    TimeSpan Duration)
{
    /// <summary>Gets whether the panel shows the row as failed (network error or status ≥ 400).</summary>
    public bool IsFailure => Error is not null || StatusCode >= 400;
}

/// <summary>Severity of a console message, as <c>console.*</c> names it.</summary>
public enum ConsoleLevel
{
    /// <summary><c>console.debug</c>.</summary>
    Debug,

    /// <summary><c>console.log</c>, <c>console.info</c>.</summary>
    Log,

    /// <summary><c>console.warn</c>.</summary>
    Warning,

    /// <summary><c>console.error</c>, uncaught errors, blocked requests.</summary>
    Error,
}

/// <summary>One line of the Console panel.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Message">Text, arguments already formatted by the page bridge.</param>
/// <param name="Source">Where it came from (<c>page.js:12</c>, <c>network</c>, <c>browser</c>), or empty.</param>
public sealed record ConsoleLogEntry(ConsoleLevel Level, string Message, string Source);

/// <summary>
/// The Network and Console logs of the developer tools (ADR 0075 §2, questionnaire 13): one per tab,
/// cleared on each navigation. Bounded, so a page that logs in a loop cannot exhaust memory.
/// </summary>
public sealed class DevToolsLog
{
    /// <summary>Maximum rows kept per panel; older rows are dropped first.</summary>
    public const int MaxEntries = 500;

    private readonly List<NetworkLogEntry> _network = [];
    private readonly List<ConsoleLogEntry> _console = [];
    private readonly object _gate = new();

    /// <summary>Raised after any change, so the panel re-renders.</summary>
    public event Action? Changed;

    /// <summary>Gets a snapshot of the Network rows, oldest first.</summary>
    public IReadOnlyList<NetworkLogEntry> Network
    {
        get
        {
            lock (_gate)
            {
                return [.. _network];
            }
        }
    }

    /// <summary>Gets a snapshot of the Console lines, oldest first.</summary>
    public IReadOnlyList<ConsoleLogEntry> Console
    {
        get
        {
            lock (_gate)
            {
                return [.. _console];
            }
        }
    }

    /// <summary>Gets the number of error lines in the console (the red badge of the toolbar button).</summary>
    public int ErrorCount
    {
        get
        {
            lock (_gate)
            {
                return _console.Count(entry => entry.Level == ConsoleLevel.Error);
            }
        }
    }

    /// <summary>Adds a Network row.</summary>
    public void Add(NetworkLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            Append(_network, entry);
        }

        Changed?.Invoke();
    }

    /// <summary>Adds a Console line.</summary>
    public void Add(ConsoleLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            Append(_console, entry);
        }

        Changed?.Invoke();
    }

    /// <summary>Clears both panels, as a navigation does without "Preserve log".</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _network.Clear();
            _console.Clear();
        }

        Changed?.Invoke();
    }

    private static void Append<T>(List<T> list, T entry)
    {
        if (list.Count >= MaxEntries)
        {
            list.RemoveAt(0);
        }

        list.Add(entry);
    }
}
