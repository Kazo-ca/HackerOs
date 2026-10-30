namespace HackerOs.AppSdk.Administration;

/// <summary>Connection of this device to the optional HackerOS server (ADR 0028), as an app sees it.</summary>
/// <param name="IsConnected">Whether the device is linked to a server account.</param>
/// <param name="ServerUrl">Server base URL, or <see langword="null"/> when not connected.</param>
/// <param name="AccountId">Server account identifier, or <see langword="null"/> when not connected.</param>
/// <param name="DeviceId">Device identifier on that server, or <see langword="null"/> when not connected.</param>
/// <remarks>The refresh and access tokens stay inside the OS: no app ever sees them (ADR 0056, section 6).</remarks>
public sealed record HackServerConnectionState(bool IsConnected, Uri? ServerUrl, Guid? AccountId, Guid? DeviceId)
{
    /// <summary>The state of a device that is not connected, the normal case without a server.</summary>
    public static HackServerConnectionState NotConnected { get; } = new(false, null, null, null);
}

/// <summary>
/// What an address answered to <see cref="HackServerConnection.TestAsync(Uri, CancellationToken)"/>
/// (ADR 0191 §6): whether a HackerOS server is there, before linking this device to it.
/// </summary>
/// <param name="IsServer">Whether a HackerOS server answered.</param>
/// <param name="ServerVersion">The server's version, when it answered.</param>
/// <param name="ApiVersion">The server's API version, when it answered.</param>
/// <param name="Error">Why the test failed (unreachable, not a HackerOS server), in English; <see langword="null"/> on success.</param>
public sealed record HackServerProbe(bool IsServer, string? ServerVersion, string? ApiVersion, string? Error);

/// <summary>What a synchronization moves (ADR 0029 to 0033).</summary>
public enum HackSyncDomain
{
    /// <summary>The user's home directory, user settings included (ADR 0030, ADR 0044 section 10).</summary>
    FileSystem = 0,

    /// <summary>Capability grants issued by the server; pull only (ADR 0031).</summary>
    Grants = 1,

    /// <summary><c>/etc/hackeros/file-associations.json</c> (ADR 0033).</summary>
    FileAssociations = 2
}

/// <summary>Direction of one synchronization pass.</summary>
public enum HackSyncDirection
{
    /// <summary>Send local changes to the server.</summary>
    Push = 0,

    /// <summary>Apply the server's changes locally.</summary>
    Pull = 1
}

/// <summary>
/// A file both this device and another one changed since their last synchronization (ADR 0030,
/// ADR 0101 section 2). The file stays frozen (kept as is, neither sent nor replaced) until the
/// user picks a version in the OS conflict dialog (<see cref="IAppServerConnectionGateway.ShowConflictsAsync"/>).
/// </summary>
/// <param name="Domain">Domain of the record.</param>
/// <param name="Path">Path of the conflicting file, relative to the home directory.</param>
/// <param name="DetectedAt">When the conflict was found, when known.</param>
public sealed record HackSyncConflict(HackSyncDomain Domain, string Path, DateTimeOffset? DetectedAt = null);

/// <summary>State of the periodic synchronization of the session user (ADR 0101 section 4).</summary>
/// <param name="IsActive">The background synchronization runs (a session is open on a host that supports it).</param>
/// <param name="IsPaused">The user paused it in <c>~/.config/hackeros/sync.config</c>.</param>
/// <param name="IsOnline">The last pass reached the server.</param>
/// <param name="LastSyncAt">End of the last completed pass, or <see langword="null"/>.</param>
/// <param name="LastSent">Records the last pass sent.</param>
/// <param name="LastReceived">Records the last pass applied locally.</param>
/// <param name="LastError">Why the last pass failed, or <see langword="null"/>.</param>
/// <param name="Interval">Interval between two periodic passes, set in <c>/etc/hackeros/sync.config</c>.</param>
public sealed record HackSyncStatus(
    bool IsActive,
    bool IsPaused,
    bool IsOnline,
    DateTimeOffset? LastSyncAt,
    int LastSent,
    int LastReceived,
    string? LastError,
    TimeSpan Interval)
{
    /// <summary>The status of a host without background synchronization.</summary>
    public static HackSyncStatus Inactive { get; } = new(false, false, false, null, 0, 0, null, TimeSpan.FromMinutes(5));
}

/// <summary>
/// The network relay policy of the connected server (ADR 0102 §5), as <c>GET /api/proxy/policy</c>
/// reports it and Settings &gt; Server Connection shows it.
/// </summary>
/// <param name="Enabled">Whether the server relays at all.</param>
/// <param name="HttpPorts">Ports an HTTP request may target; empty when <paramref name="AllowAllPorts"/>.</param>
/// <param name="TcpPorts">Ports a TCP connection may target; empty when <paramref name="AllowAllPorts"/>.</param>
/// <param name="UdpPorts">Ports a UDP socket may target; empty when <paramref name="AllowAllPorts"/>.</param>
/// <param name="AllowAllPorts">Whether every port is allowed.</param>
/// <param name="AllowPrivateNetworks">Whether the server's local network is reachable.</param>
/// <param name="QuotasEnabled">Whether the limits below apply.</param>
/// <param name="MaxRequestBytes">Largest request body.</param>
/// <param name="MaxResponseBytes">Largest response body.</param>
/// <param name="MaxConcurrentRequests">Requests in flight at once for this device.</param>
/// <param name="MaxStreams">TCP/UDP sockets open at once for this device.</param>
/// <param name="OpenStreams">TCP/UDP sockets this device has open now.</param>
/// <param name="StreamIdleTimeout">How long a socket may stay silent before the server closes it.</param>
/// <param name="GrantedApps">Apps the server lets use the relay on its own (ADR 0102 §4).</param>
/// <param name="Warnings">The settings that weaken the server's defaults, one sentence each.</param>
public sealed record HackServerRelayPolicy(
    bool Enabled,
    IReadOnlyList<int> HttpPorts,
    IReadOnlyList<int> TcpPorts,
    IReadOnlyList<int> UdpPorts,
    bool AllowAllPorts,
    bool AllowPrivateNetworks,
    bool QuotasEnabled,
    long MaxRequestBytes,
    long MaxResponseBytes,
    int MaxConcurrentRequests,
    int MaxStreams,
    int OpenStreams,
    TimeSpan StreamIdleTimeout,
    IReadOnlyList<string> GrantedApps,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The server refused a request or could not be reached while the user asked for it (connect,
/// create an account, synchronize). Never raised merely because no server is configured.
/// </summary>
public sealed class HackServerConnectionException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Human-readable reason.</param>
    /// <param name="innerException">The transport or server error.</param>
    public HackServerConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Optional server connection and synchronization for one app instance (ADR 0056, section 10),
/// behind <see cref="HackServerConnection"/>. Every member requires the dangerous capability
/// <c>server.connection.manage</c>.
/// </summary>
public interface IAppServerConnectionGateway
{
    /// <summary>Gets the current connection; <see cref="HackServerConnectionState.NotConnected"/> without a server.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<HackServerConnectionState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Links this device to an existing server account.</summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="login">Server account name.</param>
    /// <param name="password">Server account password (a credential of the remote service, not a local password).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<HackServerConnectionState> ConnectAsync(
        Uri serverUrl, string login, string password, CancellationToken cancellationToken = default);

    /// <summary>Creates a server account and links this device to it.</summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="login">New account name.</param>
    /// <param name="password">New account password.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<HackServerConnectionState> CreateAccountAsync(
        Uri serverUrl, string login, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links this device to a server account with a one-time pairing code the server printed
    /// (ADR 0191 §6), without the account's password.
    /// </summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="code">The pairing code.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackServerConnectionException">The server refused the code or could not be reached.</exception>
    ValueTask<HackServerConnectionState> PairAsync(Uri serverUrl, string code, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This server connection gateway cannot pair a device.");

    /// <summary>
    /// Asks <paramref name="serverUrl"/> whether it is a HackerOS server, without linking this device.
    /// Only called when the user asks (ADR 0191 §3: the OS never probes an address by itself).
    /// </summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<HackServerProbe> TestAsync(Uri serverUrl, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This server connection gateway cannot test an address.");

    /// <summary>Unlinks this device; local data stays the source of truth.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs one synchronization pass; does nothing when not connected.</summary>
    /// <param name="domain">What to synchronize.</param>
    /// <param name="direction">Push or pull.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    ValueTask SyncAsync(HackSyncDomain domain, HackSyncDirection direction, CancellationToken cancellationToken = default);

    /// <summary>Gets the pending synchronization conflicts of the session user, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<HackSyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the network relay policy of the connected server (ADR 0102 §5);
    /// <see langword="null"/> when the device is not connected.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackServerConnectionException">The server could not be reached.</exception>
    ValueTask<HackServerRelayPolicy?> GetRelayPolicyAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This server connection gateway cannot read the relay policy.");

    /// <summary>
    /// Opens the OS conflict dialog (ADR 0101 section 2) over every window; the user picks a version
    /// there. Does nothing when no conflict is pending.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask ShowConflictsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the state of the periodic synchronization (ADR 0101 section 4).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<HackSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses or resumes the periodic synchronization of the session user, written as
    /// <c>paused</c> in <c>~/.config/hackeros/sync.config</c>. A manual pass still runs while paused.
    /// </summary>
    /// <param name="paused">Whether to pause.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SetSyncPausedAsync(bool paused, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one complete pass now, every domain in the right order (FileSystem push then pull,
    /// Grants pull, FileAssociations push then pull); does nothing when not connected.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The status after the pass.</returns>
    ValueTask<HackSyncStatus> SyncNowAsync(CancellationToken cancellationToken = default);
}
