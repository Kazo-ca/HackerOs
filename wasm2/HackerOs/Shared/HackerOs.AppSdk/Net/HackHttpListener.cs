using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Network;

namespace HackerOs.AppSdk.Net;

/// <summary>
/// A simple HTTP server on a port of this machine, modeled on <see cref="System.Net.HttpListener"/>
/// (ADR 0074, section 2): add prefixes, start, then take requests one by one with
/// <see cref="GetContextAsync()"/> and answer each through its <see cref="HackHttpListenerContext.Response"/>.
/// </summary>
/// <remarks>
/// <para>
/// The OS records the socket in its table of listening ports (<c>netstat -tlnp</c>,
/// <c>/proc/net/tcp</c>) until <see cref="Stop"/>, <see cref="Close"/> or the end of the process.
/// Requests come from the simulated network: the browser, <c>curl</c> or any app's
/// <see cref="HackHttpClient"/> on this machine.
/// </para>
/// <para>
/// Capabilities: <c>network.listen</c>, plus <c>network.listen.privileged</c> for a port below 1024
/// (<see cref="UnauthorizedAccessException"/> otherwise). A web app may listen on the URLs of its own
/// manifest without them (ADR 0073). Difference with .NET: <see cref="StartAsync"/> is asynchronous,
/// because the socket is opened by the OS, possibly from a worker.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using HackHttpListener listener = new();
/// listener.Prefixes.Add("http://localhost:8080/");
/// await listener.StartAsync();
/// HackHttpListenerContext context = await listener.GetContextAsync();
/// context.Response.ContentType = "text/plain; charset=utf-8";
/// context.Response.Write("Hello from KazoOS");
/// context.Response.Close();
/// </code>
/// </example>
public sealed class HackHttpListener : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private IHackHttpListenerSocket? _socket;
    private bool _closed;

    /// <summary>Initializes a listener with no prefix.</summary>
    public HackHttpListener() => Prefixes = new HackHttpListenerPrefixCollection(this);

    /// <summary>Gets the prefixes served (<c>http://host:port/path/</c>); they can only change while stopped.</summary>
    public HackHttpListenerPrefixCollection Prefixes { get; }

    /// <summary>Gets whether the listener is started and not yet stopped.</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _socket is not null;
            }
        }
    }

    /// <summary>Opens the listening socket for <see cref="Prefixes"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="InvalidOperationException">No prefix was added, or the listener is already started.</exception>
    /// <exception cref="ObjectDisposedException">The listener was closed.</exception>
    /// <exception cref="UnauthorizedAccessException">The app may not listen on one of the ports.</exception>
    /// <exception cref="HackHttpListenerException">Another process already listens on one of the prefixes.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        string[] prefixes;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_socket is not null)
            {
                throw new InvalidOperationException("The listener is already started.");
            }

            prefixes = [.. Prefixes];
        }

        if (prefixes.Length == 0)
        {
            throw new InvalidOperationException("Add at least one prefix before starting the listener.");
        }

        HackNetworkResult<IHackHttpListenerSocket> result = await HackNetworkErrorTranslator
            .CallAsync(gateway => gateway.ListenHttpAsync(prefixes, cancellationToken)).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new HackHttpListenerException(result.Error, result.Message ?? $"Cannot listen: {HackNetworkErrorCodes.ToCode(result.Error)}");
        }

        bool lost;
        lock (_gate)
        {
            lost = _closed;
            if (!lost)
            {
                _socket = result.Value!;
            }
        }

        if (lost)
        {
            await result.Value!.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(HackHttpListener));
        }
    }

    /// <summary>Waits for the next request.</summary>
    /// <returns>The request and the response to fill.</returns>
    /// <exception cref="InvalidOperationException">The listener is not started.</exception>
    /// <exception cref="HackHttpListenerException">The listener was stopped while waiting.</exception>
    public Task<HackHttpListenerContext> GetContextAsync() => GetContextAsync(CancellationToken.None);

    /// <summary>Waits for the next request, or until <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="cancellationToken">Cancels the wait; the listener stays open.</param>
    /// <returns>The request and the response to fill.</returns>
    /// <exception cref="InvalidOperationException">The listener is not started.</exception>
    /// <exception cref="HackHttpListenerException">The listener was stopped while waiting.</exception>
    public async Task<HackHttpListenerContext> GetContextAsync(CancellationToken cancellationToken)
    {
        IHackHttpListenerSocket socket;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed && _socket is null, this);
            socket = _socket ?? throw new InvalidOperationException("Call StartAsync before GetContextAsync.");
        }

        HackHttpListenerExchange? exchange = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return exchange is null
            ? throw new HackHttpListenerException(HackNetworkError.ConnectionFailed, "The listener was stopped.")
            : new HackHttpListenerContext(socket, exchange);
    }

    /// <summary>Closes the socket: the port is freed and pending <see cref="GetContextAsync()"/> calls fail. Prefixes are kept.</summary>
    public void Stop() => _ = StopAsync();

    /// <summary>Closes the socket and waits until the OS freed the port.</summary>
    public async ValueTask StopAsync()
    {
        IHackHttpListenerSocket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is not null)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops the listener for good.</summary>
    public void Close() => _ = DisposeAsync();

    /// <summary>Stops the listener for good, without waiting for pending responses (same as <see cref="Close"/>).</summary>
    public void Abort() => Close();

    /// <inheritdoc />
    public void Dispose() => Close();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Throws when the prefixes may not change (the listener is started or closed).</summary>
    internal void EnsureStopped()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_socket is not null)
            {
                throw new InvalidOperationException("Stop the listener before changing its prefixes.");
            }
        }
    }
}

/// <summary>The prefixes of a <see cref="HackHttpListener"/>; each is checked when added, like <c>HttpListenerPrefixCollection</c>.</summary>
public sealed class HackHttpListenerPrefixCollection : ICollection<string>
{
    private readonly HackHttpListener _owner;
    private readonly List<string> _items = [];

    internal HackHttpListenerPrefixCollection(HackHttpListener owner) => _owner = owner;

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>Adds a prefix: <c>http://host[:port]/path/</c>, with <c>+</c> or <c>*</c> for any host.</summary>
    /// <param name="item">The prefix.</param>
    /// <exception cref="ArgumentException">The prefix is malformed (no trailing <c>/</c>, bad port…).</exception>
    public void Add(string item)
    {
        _owner.EnsureStopped();
        if (!WebAppUrls.TryParse(item, allowWildcardHost: true, out WebBinding binding, out string? error))
        {
            throw new ArgumentException(error, nameof(item));
        }

        string normalized = binding.ToString();
        if (!_items.Contains(normalized, StringComparer.Ordinal))
        {
            _items.Add(normalized);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _owner.EnsureStopped();
        _items.Clear();
    }

    /// <inheritdoc />
    public bool Contains(string item) =>
        WebAppUrls.TryParse(item, allowWildcardHost: true, out WebBinding binding, out _) && _items.Contains(binding.ToString(), StringComparer.Ordinal);

    /// <inheritdoc />
    public void CopyTo(string[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public bool Remove(string item)
    {
        _owner.EnsureStopped();
        return WebAppUrls.TryParse(item, allowWildcardHost: true, out WebBinding binding, out _) && _items.Remove(binding.ToString());
    }

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One request received by a <see cref="HackHttpListener"/> and the response to send back.</summary>
public sealed class HackHttpListenerContext
{
    internal HackHttpListenerContext(IHackHttpListenerSocket socket, HackHttpListenerExchange exchange)
    {
        Request = new HackHttpListenerRequest(exchange);
        Response = new HackHttpListenerResponse(socket, exchange.Id);
    }

    /// <summary>Gets the request.</summary>
    public HackHttpListenerRequest Request { get; }

    /// <summary>Gets the response; nothing is sent until <see cref="HackHttpListenerResponse.Close()"/>.</summary>
    public HackHttpListenerResponse Response { get; }
}

/// <summary>The request of a <see cref="HackHttpListenerContext"/>, modeled on <c>HttpListenerRequest</c>.</summary>
public sealed class HackHttpListenerRequest
{
    private readonly HackHttpListenerExchange _exchange;
    private MemoryStream? _input;

    internal HackHttpListenerRequest(HackHttpListenerExchange exchange)
    {
        _exchange = exchange;
        Headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in exchange.Headers)
        {
            Headers.Add(header.Key, header.Value);
        }

        if (exchange.ContentType is not null && Headers["Content-Type"] is null)
        {
            Headers.Add("Content-Type", exchange.ContentType);
        }

        QueryString = new NameValueCollection(StringComparer.Ordinal);
        string query = exchange.Url.Query;
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string name = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            QueryString.Add(name, value);
        }

        Dictionary<string, string> cookies = new(StringComparer.Ordinal);
        foreach (string? header in Headers.GetValues("Cookie") ?? [])
        {
            foreach (string part in (header ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    cookies[part[..equals]] = part[(equals + 1)..];
                }
            }
        }

        Cookies = cookies;
    }

    /// <summary>Gets the HTTP method, upper case.</summary>
    public string HttpMethod => _exchange.Method;

    /// <summary>Gets the absolute URL requested.</summary>
    public Uri Url => _exchange.Url;

    /// <summary>Gets the path and query, as sent on the request line.</summary>
    public string RawUrl => _exchange.Url.PathAndQuery;

    /// <summary>Gets the request headers (names compared without case).</summary>
    public NameValueCollection Headers { get; }

    /// <summary>Gets the query string fields, decoded.</summary>
    public NameValueCollection QueryString { get; }

    /// <summary>Gets the cookies sent by the client.</summary>
    public IReadOnlyDictionary<string, string> Cookies { get; }

    /// <summary>Gets the <c>Host</c> header (host and port).</summary>
    public string UserHostName => Headers["Host"] ?? _exchange.Url.Authority;

    /// <summary>Gets the <c>User-Agent</c> header, or <see langword="null"/>.</summary>
    public string? UserAgent => Headers["User-Agent"];

    /// <summary>Gets the <c>Referer</c> header as a URI, or <see langword="null"/>.</summary>
    public Uri? UrlReferrer => Uri.TryCreate(Headers["Referer"], UriKind.Absolute, out Uri? referrer) ? referrer : null;

    /// <summary>Gets the accepted media types of the <c>Accept</c> header.</summary>
    public string[] AcceptTypes => (Headers["Accept"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Gets the body's <c>Content-Type</c>, or <see langword="null"/>.</summary>
    public string? ContentType => _exchange.ContentType;

    /// <summary>Gets the body's encoding (the charset of <see cref="ContentType"/>, UTF-8 when none).</summary>
    public Encoding ContentEncoding => SimulatedMediaTypes.GetEncoding(ContentType);

    /// <summary>Gets the body length in bytes.</summary>
    public long ContentLength64 => _exchange.Body.LongLength;

    /// <summary>Gets whether the request has a body.</summary>
    public bool HasEntityBody => _exchange.Body.Length > 0;

    /// <summary>Gets the body bytes.</summary>
    public ReadOnlyMemory<byte> Body => _exchange.Body;

    /// <summary>Gets the body as a read-only stream.</summary>
    public Stream InputStream => _input ??= new MemoryStream(_exchange.Body, writable: false);

    /// <summary>Gets the client's address and port.</summary>
    public IPEndPoint RemoteEndPoint => _exchange.RemoteEndPoint;

    /// <summary>Gets the address and port the request arrived on.</summary>
    public IPEndPoint LocalEndPoint => _exchange.LocalEndPoint;

    /// <summary>Gets whether the client is this machine (always, until machines talk to each other).</summary>
    public bool IsLocal => IPAddress.IsLoopback(RemoteEndPoint.Address) || RemoteEndPoint.Address.Equals(LocalEndPoint.Address);

    /// <summary>Gets whether the URL used <c>https</c> (served as plain HTTP by the simulated network).</summary>
    public bool IsSecureConnection => _exchange.Url.Scheme == Uri.UriSchemeHttps;

    /// <summary>Decodes the body with <see cref="ContentEncoding"/>.</summary>
    public string ReadBodyAsString() => ContentEncoding.GetString(_exchange.Body);

    /// <summary>Reads a <c>application/x-www-form-urlencoded</c> body; empty for any other type.</summary>
    public IReadOnlyDictionary<string, string> ReadForm() =>
        HasEntityBody ? new SimulatedHttpBody(ContentType ?? SimulatedMediaTypes.OctetStream, _exchange.Body).ReadAsForm() : new Dictionary<string, string>();
}

/// <summary>
/// The response of a <see cref="HackHttpListenerContext"/>, modeled on <c>HttpListenerResponse</c>:
/// set the status and headers, write the body to <see cref="OutputStream"/>, then <see cref="Close()"/>.
/// </summary>
public sealed class HackHttpListenerResponse : IDisposable
{
    private readonly IHackHttpListenerSocket _socket;
    private readonly long _exchangeId;
    private readonly MemoryStream _output = new();
    private bool _sent;

    internal HackHttpListenerResponse(IHackHttpListenerSocket socket, long exchangeId)
    {
        _socket = socket;
        _exchangeId = exchangeId;
    }

    /// <summary>Gets or sets the status code (200 by default).</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Gets or sets the reason phrase; the standard one of <see cref="StatusCode"/> when <see langword="null"/>.</summary>
    public string? StatusDescription { get; set; }

    /// <summary>Gets or sets the body's <c>Content-Type</c>; <see langword="null"/> sends none.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the encoding <see cref="Write"/> uses (UTF-8 by default).</summary>
    public Encoding ContentEncoding { get; set; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gets the length written so far to <see cref="OutputStream"/>.</summary>
    public long ContentLength64 => _output.Length;

    /// <summary>Gets the response headers (<c>Content-Type</c> and <c>Content-Length</c> are set from the body).</summary>
    public NameValueCollection Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the stream the body is written to.</summary>
    public Stream OutputStream => _output;

    /// <summary>Gets or sets the <c>Location</c> header.</summary>
    public string? RedirectLocation
    {
        get => Headers["Location"];
        set
        {
            Headers.Remove("Location");
            if (value is not null)
            {
                Headers.Add("Location", value);
            }
        }
    }

    /// <summary>Gets whether the response was already sent.</summary>
    public bool IsClosed => _sent;

    /// <summary>Sets a header, replacing any value of the same name.</summary>
    public void AddHeader(string name, string value) => Headers.Set(name, value);

    /// <summary>Adds a value to a header, keeping the others (several <c>Set-Cookie</c>…).</summary>
    public void AppendHeader(string name, string value) => Headers.Add(name, value);

    /// <summary>Adds a <c>Set-Cookie</c> header: the client stores it for this host.</summary>
    /// <param name="name">Cookie name.</param>
    /// <param name="value">Cookie value.</param>
    /// <param name="path">Cookie path; <c>/</c> by default.</param>
    public void SetCookie(string name, string value, string path = "/") =>
        Headers.Add("Set-Cookie", $"{name}={value}; Path={path}");

    /// <summary>Redirects the client with a <c>302 Found</c> to <paramref name="url"/>.</summary>
    public void Redirect(string url)
    {
        StatusCode = 302;
        RedirectLocation = url;
    }

    /// <summary>Writes text to the body with <see cref="ContentEncoding"/>.</summary>
    public void Write(string text)
    {
        byte[] bytes = ContentEncoding.GetBytes(text ?? string.Empty);
        _output.Write(bytes);
    }

    /// <summary>Sends the response; later writes are ignored.</summary>
    public void Close()
    {
        if (_sent)
        {
            return;
        }

        _sent = true;
        List<KeyValuePair<string, string>> headers = [];
        foreach (string? name in Headers.AllKeys)
        {
            if (name is null
                || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string value in Headers.GetValues(name) ?? [])
            {
                headers.Add(new(name, value));
            }
        }

        byte[] body = _output.ToArray();
        string? contentType = ContentType ?? Headers["Content-Type"];
        if (contentType is null && body.Length > 0)
        {
            // HttpListener sends no type; browsers sniff. Say what a text body most likely is.
            contentType = "application/octet-stream";
        }

        _socket.Respond(_exchangeId, new HackHttpListenerReply(StatusCode, StatusDescription, headers, contentType, body));
    }

    /// <summary>Sends <paramref name="responseEntity"/> as the whole body, then closes.</summary>
    /// <param name="responseEntity">The body.</param>
    /// <param name="willBlock">Ignored: the OS never blocks the sender.</param>
    public void Close(byte[] responseEntity, bool willBlock)
    {
        ArgumentNullException.ThrowIfNull(responseEntity);
        _output.SetLength(0);
        _output.Write(responseEntity);
        Close();
    }

    /// <summary>Drops the response: the client gets an empty reply (<c>ERR_EMPTY_RESPONSE</c>, here a 502).</summary>
    public void Abort()
    {
        if (_sent)
        {
            return;
        }

        _sent = true;
        _socket.Respond(_exchangeId, new HackHttpListenerReply(
            502, "Bad Gateway", [], "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("The server closed the connection without answering.")));
    }

    /// <inheritdoc />
    public void Dispose() => Close();
}

/// <summary>
/// Thrown by <see cref="HackHttpListener"/> when the OS refuses the socket (another process already
/// listens there: <c>EADDRINUSE</c>) or when the listener is stopped during a wait.
/// </summary>
public sealed class HackHttpListenerException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public HackHttpListenerException(HackNetworkError networkError, string message)
        : base(message)
    {
        NetworkError = networkError;
        ErrorCode = networkError == HackNetworkError.AddressInUse ? 98 : 0;
    }

    /// <summary>Gets the network error behind the failure.</summary>
    public HackNetworkError NetworkError { get; }

    /// <summary>Gets the Linux <c>errno</c>: 98 (<c>EADDRINUSE</c>) when the address is taken, otherwise 0.</summary>
    public int ErrorCode { get; }

    /// <summary>Formats the error code, e.g. <c>EADDRINUSE (98)</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{GetType().Name} ({(ErrorCode == 98 ? "EADDRINUSE" : "ERROR")} {ErrorCode}): {Message}");
}
