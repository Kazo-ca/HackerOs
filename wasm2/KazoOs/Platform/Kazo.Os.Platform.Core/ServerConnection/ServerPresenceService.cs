using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Server.Contracts.Hosting;
using Kazo.Os.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerState = Kazo.Os.Simulation.Abstractions.ServerConnection.ServerConnectionState;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>What the notification-area icon shows about the optional server (ADR 0134 §2).</summary>
public enum ServerPresenceStatus
{
    /// <summary>No server is configured and none served the OS: the icon is hidden.</summary>
    NotConfigured = 0,

    /// <summary>The OS is reaching the server.</summary>
    Connecting = 1,

    /// <summary>The server answers and this device holds an account on it.</summary>
    Connected = 2,

    /// <summary>The server answers, but no account is linked yet: the user can identify from the icon.</summary>
    ConnectedAnonymous = 3,

    /// <summary>The server cannot be reached; the OS keeps working and retries with a growing delay.</summary>
    Offline = 4,

    /// <summary>The server answered with something the OS cannot use (incompatible API version, refusal).</summary>
    Error = 5,
}

/// <summary>A snapshot of the optional server connection, for the notification-area icon.</summary>
/// <param name="Status">Current state.</param>
/// <param name="ServerUrl">The server address, or <see langword="null"/> when none is configured.</param>
/// <param name="AccountId">The linked server account, or <see langword="null"/>.</param>
/// <param name="ServedByServer">Whether this server delivered the OS (ADR 0130), rather than being entered by hand.</param>
/// <param name="ServerVersion">The server's software version, once it answered.</param>
/// <param name="Detail">Why the state is <see cref="ServerPresenceStatus.Offline"/> or <see cref="ServerPresenceStatus.Error"/>.</param>
public sealed record ServerPresence(
    ServerPresenceStatus Status,
    Uri? ServerUrl = null,
    Guid? AccountId = null,
    bool ServedByServer = false,
    string? ServerVersion = null,
    string? Detail = null)
{
    /// <summary>Gets the state with no server at all.</summary>
    public static ServerPresence NotConfigured { get; } = new(ServerPresenceStatus.NotConfigured);
}

/// <summary>
/// Keeps the OS connected to its optional server without asking anything (ADR 0134 §1): the server
/// that served the OS, or the one linked by hand in Settings. It checks that the server answers and
/// speaks a compatible API, retries a lost server with a growing delay, and lets the user identify
/// from the notification-area icon. The OS never waits on it (AGENTS.md I.1): every failure only
/// changes <see cref="Current"/>.
/// </summary>
/// <remarks>
/// <para>
/// "Connected" covers the server link itself. Synchronization and the network relay need an
/// account: when this device holds one (ADR 0028, refresh token kept in IndexedDB) the existing
/// services use it as before; otherwise the state is <see cref="ServerPresenceStatus.ConnectedAnonymous"/>
/// until <see cref="IdentifyAsync"/> links one.
/// </para>
/// <para>
/// An administrator turns the automatic connection off with <c>auto-connect=false</c> in
/// <c>/etc/kazoos/server.config</c>; the OS then only uses a server linked by hand in Settings.
/// </para>
/// </remarks>
public sealed class ServerPresenceService : IAsyncDisposable
{
    /// <summary>The machine file read at start (ADR 0134 §1).</summary>
    public static OsSettingsFile ConfigFile { get; } = new(OsSettingsLocation.System, "server.config");

    /// <summary>The API major version this OS speaks (<c>/api/version</c>, ADR 0028).</summary>
    public const string SupportedApiMajorVersion = "1";

    /// <summary>Retry delays after a failure; the last one repeats.</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)];

    /// <summary>While connected, the server is checked again at this interval to notice its loss.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IServerConnectionService _connection;
    private readonly IHostInfoClient _hostInfo;
    private readonly Func<CancellationToken, ValueTask<string?>>? _readConfig;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _loop;
    private Task _loopTask = Task.CompletedTask;
    private Uri? _servingServer;
    private bool _servingServerProbed;
    private ServerPresence _current = ServerPresence.NotConfigured;

    /// <summary>Creates the service.</summary>
    /// <param name="connection">The device's server account link (ADR 0028).</param>
    /// <param name="hostInfo">Host discovery and server check.</param>
    /// <param name="readConfig">
    /// Reads <c>/etc/kazoos/server.config</c> (<see cref="ConfigFile"/>, through the OS settings
    /// files); <see langword="null"/> means automatic connection on.
    /// </param>
    /// <param name="logger">Receives one line per state change.</param>
    /// <param name="delay">Waits between attempts; replaced by tests.</param>
    public ServerPresenceService(
        IServerConnectionService connection,
        IHostInfoClient hostInfo,
        Func<CancellationToken, ValueTask<string?>>? readConfig = null,
        ILogger<ServerPresenceService>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _hostInfo = hostInfo ?? throw new ArgumentNullException(nameof(hostInfo));
        _readConfig = readConfig;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    /// <summary>Raised after every change of <see cref="Current"/>, on any thread.</summary>
    public event Action? Changed;

    /// <summary>Gets the latest state.</summary>
    public ServerPresence Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Starts (or restarts) the connection loop: finds the server, checks it, and keeps checking it.
    /// Returns once the first check is done, never throws on a server problem.
    /// </summary>
    /// <param name="cancellationToken">Cancels the first check only; the loop runs until <see cref="StopAsync"/>.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        CancellationTokenSource loop = new();
        TaskCompletionSource firstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task = RunAsync(firstCheck, loop.Token);
        lock (_gate)
        {
            _loop = loop;
            _loopTask = task;
        }

        using (cancellationToken.Register(() => firstCheck.TrySetCanceled(cancellationToken)))
        {
            await firstCheck.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Stops the loop; <see cref="Current"/> keeps its last value.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? loop;
        Task task;
        lock (_gate)
        {
            loop = _loop;
            task = _loopTask;
            _loop = null;
        }

        if (loop is null)
        {
            return;
        }

        await loop.CancelAsync().ConfigureAwait(false);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        loop.Dispose();
    }

    /// <summary>"Reconnect" in the icon's panel: checks the server again now, without waiting for the next retry.</summary>
    public Task ReconnectAsync() => StartAsync();

    /// <summary>
    /// "Identify" in the icon's panel: links this device to an account on the current server, then
    /// checks the server again.
    /// </summary>
    /// <param name="login">Server account name.</param>
    /// <param name="password">Server account password.</param>
    /// <param name="createAccount">Whether to create the account rather than log into an existing one.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="InvalidOperationException">No server is known.</exception>
    /// <exception cref="ServerConnectionException">The server refused the credentials.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached.</exception>
    public async Task IdentifyAsync(string login, string password, bool createAccount, CancellationToken cancellationToken = default)
    {
        Uri server = Current.ServerUrl ?? throw new InvalidOperationException("No Kazo OS server is known.");
        if (createAccount)
        {
            await _connection.ConnectWithNewAccountAsync(server, login, password, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _connection.ConnectWithExistingAccountAsync(server, login, password, cancellationToken).ConfigureAwait(false);
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(TaskCompletionSource firstCheck, CancellationToken cancellationToken)
    {
        int failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServerPresence result = await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
                Publish(result);
                firstCheck.TrySetResult();
                switch (result.Status)
                {
                    case ServerPresenceStatus.NotConfigured:
                        return;
                    case ServerPresenceStatus.Connected or ServerPresenceStatus.ConnectedAnonymous:
                        failures = 0;
                        await _delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        await _delay(RetryDelays[Math.Min(failures, RetryDelays.Length - 1)], cancellationToken).ConfigureAwait(false);
                        failures++;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            firstCheck.TrySetResult();
        }
    }

    private async Task<ServerPresence> CheckOnceAsync(CancellationToken cancellationToken)
    {
        ServerState? account = await ReadAccountAsync(cancellationToken).ConfigureAwait(false);
        Uri? servingServer = await FindServingServerAsync(cancellationToken).ConfigureAwait(false);
        Uri? server = account is not null && Uri.TryCreate(account.ServerBaseUrl, UriKind.Absolute, out Uri? linked)
            ? linked
            : servingServer;
        if (server is null)
        {
            return ServerPresence.NotConfigured;
        }

        bool servedByServer = servingServer is not null && UrisMatch(servingServer, server);
        Guid? accountId = account?.AccountId;
        if (Current.Status is not (ServerPresenceStatus.Connected or ServerPresenceStatus.ConnectedAnonymous))
        {
            Publish(new ServerPresence(ServerPresenceStatus.Connecting, server, accountId, servedByServer));
        }

        try
        {
            HostInfoResponse info = await _hostInfo.GetHostInfoAsync(server, cancellationToken).ConfigureAwait(false);
            if (!IsSupportedApi(info.ApiVersion))
            {
                return new ServerPresence(
                    ServerPresenceStatus.Error, server, accountId, servedByServer, info.ServerVersion,
                    $"API {info.ApiVersion ?? "?"}; this OS speaks API {SupportedApiMajorVersion}.x");
            }

            return new ServerPresence(
                accountId is null ? ServerPresenceStatus.ConnectedAnonymous : ServerPresenceStatus.Connected,
                server, accountId, servedByServer, info.ServerVersion);
        }
        catch (ServerConnectionException exception)
        {
            return new ServerPresence(ServerPresenceStatus.Error, server, accountId, servedByServer, Detail: exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ServerPresence(ServerPresenceStatus.Offline, server, accountId, servedByServer, Detail: exception.Message);
        }
    }

    private async Task<ServerState?> ReadAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "server: the device's server link could not be read");
            return null;
        }
    }

    /// <summary>The origin never changes during a page's life: it is probed once, if allowed.</summary>
    private async Task<Uri?> FindServingServerAsync(CancellationToken cancellationToken)
    {
        if (_servingServerProbed)
        {
            return _servingServer;
        }

        if (!await IsAutoConnectEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        _servingServer = await _hostInfo.FindServingServerAsync(cancellationToken).ConfigureAwait(false);
        _servingServerProbed = true;
        if (_servingServer is not null)
        {
            _logger.LogInformation("server: this OS was served by the Kazo OS server {Server}; connecting automatically", _servingServer);
        }

        return _servingServer;
    }

    /// <summary>Reads <c>auto-connect</c> (or <c>[server] auto-connect</c>); on by default, and on when the file cannot be read.</summary>
    private async Task<bool> IsAutoConnectEnabledAsync(CancellationToken cancellationToken)
    {
        if (_readConfig is null)
        {
            return true;
        }

        string? content;
        try
        {
            content = await _readConfig(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "server: /etc/kazoos/server.config could not be read; automatic connection stays on");
            return true;
        }

        return ParseAutoConnect(content);
    }

    /// <summary>Parses <c>/etc/kazoos/server.config</c>: only <c>false</c>, <c>no</c>, <c>off</c> or <c>0</c> turn it off.</summary>
    /// <param name="content">File content, or <see langword="null"/> when absent.</param>
    public static bool ParseAutoConnect(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        ConfigDocumentParseResult parsed = ConfigDocumentFormat.Parse(content);
        foreach ((string key, string value) in parsed.Values)
        {
            if (key is "auto-connect" or "server.auto-connect")
            {
                return value.Trim().ToLowerInvariant() is not ("false" or "no" or "off" or "0");
            }
        }

        return true;
    }

    private static bool IsSupportedApi(string? apiVersion) =>
        apiVersion is null
        || string.Equals(apiVersion.Split('.')[0], SupportedApiMajorVersion, StringComparison.Ordinal);

    private static bool UrisMatch(Uri left, Uri right) =>
        Uri.Compare(left, right, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private void Publish(ServerPresence presence)
    {
        lock (_gate)
        {
            if (_current == presence)
            {
                return;
            }

            _current = presence;
        }

        _logger.LogInformation("server: {Status} {Server} {Detail}", presence.Status, presence.ServerUrl, presence.Detail);
        Changed?.Invoke();
    }
}
