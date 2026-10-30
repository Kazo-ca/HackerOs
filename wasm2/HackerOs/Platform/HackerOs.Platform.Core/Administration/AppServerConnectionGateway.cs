using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Server.Contracts.Proxy;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using ServerState = HackerOs.Simulation.Abstractions.ServerConnection.ServerConnectionState;

namespace HackerOs.Platform.Core.Administration;

/// <summary>
/// Platform services behind <see cref="AppServerConnectionGateway"/>, resolved lazily: none of them
/// exists on a host built without the optional server plumbing.
/// </summary>
/// <param name="Connection">Server account link (ADR 0028).</param>
/// <param name="FileSystemSync">Home directory sync (ADR 0030).</param>
/// <param name="GrantsSync">Grant pull (ADR 0031).</param>
/// <param name="FileAssociationsSync">File association sync (ADR 0033).</param>
/// <param name="Proxy">Network relay client, read for its policy (ADR 0102 §5).</param>
/// <param name="Scheduler">Periodic synchronization (ADR 0101 section 4).</param>
/// <param name="ConflictCenter">The OS conflict dialog's state (ADR 0101 section 2).</param>
/// <param name="Settings">Writes <c>~/.config/hackeros/sync.config</c>.</param>
public sealed record ServerConnectionGatewayServices(
    Func<IServerConnectionService> Connection,
    Func<IFileSystemSyncService>? FileSystemSync = null,
    Func<IGrantsSyncService>? GrantsSync = null,
    Func<IFileAssociationsSyncService>? FileAssociationsSync = null,
    Func<IProxyClient>? Proxy = null,
    Func<SyncScheduler>? Scheduler = null,
    Func<SyncConflictCenter>? ConflictCenter = null,
    Func<IOsSettingsService>? Settings = null)
{
    /// <summary>
    /// Host discovery client, for <see cref="IAppServerConnectionGateway.TestAsync"/> (ADR 0191 §6);
    /// without it a test reports that this host cannot test an address.
    /// </summary>
    public Func<IHostInfoClient>? HostInfo { get; init; }
}

/// <summary>
/// Trusted <see cref="IAppServerConnectionGateway"/> (ADR 0056, section 10) over the server
/// connection and sync services of Platform Core.
/// </summary>
/// <remarks>
/// <para>
/// Every call requires <c>server.connection.manage</c>. The tokens stay here: the app only sees the
/// server URL and the account and device identifiers.
/// </para>
/// <para>
/// The server is optional (<c>AGENTS.md</c>): with no services the state is "not connected" and a
/// sync does nothing. Transport and server errors of a request the user made are rethrown as
/// <see cref="HackServerConnectionException"/>.
/// </para>
/// </remarks>
internal sealed class AppServerConnectionGateway : IAppServerConnectionGateway
{
    private readonly ServerConnectionGatewayServices? _services;
    private readonly ICapabilityChecker _capabilities;
    private readonly Func<System.Globalization.CultureInfo> _language;

    /// <summary>
    /// The relay warnings the server writes (<c>ProxyRelayPolicy.OperatorWeakeningWarnings</c>, always
    /// in English), with the core text that says each one in the app's language.
    /// </summary>
    private static readonly Dictionary<string, string> RelayWarningKeys = new(StringComparer.Ordinal)
    {
        ["HackerOs:Proxy:AllowAllPorts is on: the relay reaches every TCP and UDP port."] = "Relay.Warning.AllowAllPorts",
        ["HackerOs:Proxy:AllowPrivateNetworks is on: the relay reaches the private networks of the server's machine (loopback and cloud metadata stay blocked)."] = "Relay.Warning.AllowPrivateNetworks",
        ["HackerOs:Proxy:Quotas:Enabled is off: no size, concurrency, stream or idle limit applies."] = "Relay.Warning.QuotasDisabled"
    };

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="services">Server services; <see langword="null"/> on a host without them.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="language">
    /// Language of the calling process (ADR 0104), read at each call: the relay warnings are shown in
    /// it. English when <see langword="null"/>.
    /// </param>
    public AppServerConnectionGateway(
        ServerConnectionGatewayServices? services,
        ICapabilityChecker capabilities,
        Func<System.Globalization.CultureInfo>? language = null)
    {
        _services = services;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _language = language ?? (static () => HackerOs.AppSdk.Globalization.HackLocale.DefaultLanguage);
    }

    /// <inheritdoc />
    public async ValueTask<HackServerConnectionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services is null)
        {
            return HackServerConnectionState.NotConnected;
        }

        return ToPublic(await _services.Connection().GetStateAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public ValueTask<HackServerConnectionState> ConnectAsync(
        Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(serverUrl, login, password, createAccount: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask<HackServerConnectionState> CreateAccountAsync(
        Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(serverUrl, login, password, createAccount: true, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<HackServerConnectionState> PairAsync(Uri serverUrl, string code, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services is null)
        {
            throw new HackServerConnectionException("This HackerOS host has no optional-server support.");
        }

        try
        {
            ServerState state = await _services.Connection()
                .ConnectWithPairingCodeAsync(serverUrl, code, cancellationToken).ConfigureAwait(false);

            // ADR 0101 section 4: a (re)connection triggers a pass.
            _services.Scheduler?.Invoke().RequestSync();
            return ToPublic(state);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException)
        {
            throw new HackServerConnectionException($"Could not pair: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<HackServerProbe> TestAsync(Uri serverUrl, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (_services?.HostInfo is not { } hostInfo)
        {
            return new HackServerProbe(false, null, null, "This HackerOS host cannot test a server address.");
        }

        try
        {
            HackerOs.Server.Contracts.Hosting.HostInfoResponse info =
                await hostInfo().GetHostInfoAsync(serverUrl, cancellationToken).ConfigureAwait(false);
            return info.IsServer
                ? new HackServerProbe(true, info.ServerVersion, info.ApiVersion, null)
                : new HackServerProbe(false, null, null, $"{serverUrl} answered, but not as a HackerOS server.");
        }
        catch (ServerConnectionException exception)
        {
            return new HackServerProbe(false, null, null, exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A browser reports a refused connection, a blocked one (mixed content, an untrusted
            // certificate, CORS) and an unknown name the same way: say which ones to check.
            return new HackServerProbe(false, null, null,
                $"{serverUrl} could not be reached (is the server started? for HTTPS, is its certificate approved on this device?): {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services is not null)
        {
            await _services.Connection().DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask SyncAsync(HackSyncDomain domain, HackSyncDirection direction, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (domain == HackSyncDomain.Grants && direction == HackSyncDirection.Push)
        {
            throw new ArgumentException("Grants are issued by the server: this domain can only be pulled (ADR 0031).", nameof(direction));
        }

        if (_services is null)
        {
            return;
        }

        try
        {
            Task pass = (domain, direction) switch
            {
                (HackSyncDomain.FileSystem, HackSyncDirection.Push) => _services.FileSystemSync?.Invoke().PushAsync(cancellationToken),
                (HackSyncDomain.FileSystem, HackSyncDirection.Pull) => _services.FileSystemSync?.Invoke().PullAsync(cancellationToken),
                (HackSyncDomain.Grants, _) => _services.GrantsSync?.Invoke().PullAsync(cancellationToken),
                (HackSyncDomain.FileAssociations, HackSyncDirection.Push) => _services.FileAssociationsSync?.Invoke().PushAsync(cancellationToken),
                (HackSyncDomain.FileAssociations, HackSyncDirection.Pull) => _services.FileAssociationsSync?.Invoke().PullAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unknown sync domain.")
            } ?? Task.CompletedTask;
            await pass.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException)
        {
            throw new HackServerConnectionException($"Sync failed: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackSyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services?.FileSystemSync is not { } fileSystemSync)
        {
            return [];
        }

        IReadOnlyList<SyncFileConflict> conflicts = await fileSystemSync().GetConflictsAsync(cancellationToken).ConfigureAwait(false);
        return [.. conflicts.Select(conflict => new HackSyncConflict(HackSyncDomain.FileSystem, conflict.RelativePath, conflict.DetectedAtUtc))];
    }

    /// <inheritdoc />
    public async ValueTask<HackServerRelayPolicy?> GetRelayPolicyAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services?.Proxy is not { } proxyFactory)
        {
            return null;
        }

        IServerConnectionService connection = _services.Connection();
        try
        {
            ServerState? state = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is null || await connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
            {
                return null;
            }

            ProxyPolicyResponse policy = await proxyFactory()
                .GetPolicyAsync(new Uri(state.ServerBaseUrl), token, cancellationToken)
                .ConfigureAwait(false);
            return new HackServerRelayPolicy(
                policy.Enabled,
                policy.HttpPorts ?? [],
                policy.TcpPorts ?? [],
                policy.UdpPorts ?? [],
                policy.AllowAllPorts,
                policy.AllowPrivateNetworks,
                policy.QuotasEnabled,
                policy.MaxRequestBytes,
                policy.MaxPayloadBytes,
                policy.MaxConcurrentRequests,
                policy.MaxStreamsPerDevice,
                policy.CurrentStreams,
                TimeSpan.FromSeconds(policy.StreamIdleTimeoutSeconds),
                policy.GrantedApps ?? [],
                LocalizeRelayWarnings(policy.OperatorWeakeningWarnings));
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException)
        {
            throw new HackServerConnectionException($"Could not read the relay policy: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Rewrites the server's known relay warnings in the app's language; a warning this client
    /// does not know (a newer server) is shown as the server wrote it.
    /// </summary>
    private IReadOnlyList<string> LocalizeRelayWarnings(IReadOnlyList<string>? warnings)
    {
        if (warnings is null || warnings.Count == 0)
        {
            return [];
        }

        Microsoft.Extensions.Localization.IStringLocalizer text = Strings.For(_language());
        return [.. warnings.Select(warning => RelayWarningKeys.TryGetValue(warning, out string? key) ? text[key].Value : warning)];
    }

    public async ValueTask ShowConflictsAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services?.ConflictCenter is { } center)
        {
            await center().OpenAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask<HackSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        return ValueTask.FromResult(_services?.Scheduler is { } scheduler ? ToPublic(scheduler().Status) : HackSyncStatus.Inactive);
    }

    /// <inheritdoc />
    public async ValueTask SetSyncPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services?.Settings is not { } settings)
        {
            throw new HackServerConnectionException("This HackerOS host has no periodic synchronization.");
        }

        await settings().SetAsync(
            SyncSettingsDocuments.Document, SyncSettingsDocuments.PausedKey, paused ? "true" : "false", SettingLayer.User,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!paused)
        {
            _services.Scheduler?.Invoke().RequestSync();
        }
    }

    /// <inheritdoc />
    public async ValueTask<HackSyncStatus> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services is null)
        {
            return HackSyncStatus.Inactive;
        }

        if (_services.Scheduler is not { } scheduler)
        {
            // A host without the scheduler: the same order, one domain after the other.
            await SyncAsync(HackSyncDomain.FileSystem, HackSyncDirection.Push, cancellationToken).ConfigureAwait(false);
            await SyncAsync(HackSyncDomain.FileSystem, HackSyncDirection.Pull, cancellationToken).ConfigureAwait(false);
            await SyncAsync(HackSyncDomain.Grants, HackSyncDirection.Pull, cancellationToken).ConfigureAwait(false);
            await SyncAsync(HackSyncDomain.FileAssociations, HackSyncDirection.Push, cancellationToken).ConfigureAwait(false);
            await SyncAsync(HackSyncDomain.FileAssociations, HackSyncDirection.Pull, cancellationToken).ConfigureAwait(false);
            return HackSyncStatus.Inactive;
        }

        try
        {
            await scheduler().SyncNowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or IOException)
        {
            throw new HackServerConnectionException($"Sync failed: {exception.Message}", exception);
        }

        return ToPublic(scheduler().Status);
    }

    private static HackSyncStatus ToPublic(SyncSchedulerStatus status) => new(
        status.IsActive,
        status.IsPaused,
        status.IsOnline,
        status.LastPassAtUtc,
        status.LastResult?.Sent ?? 0,
        status.LastResult?.Received ?? 0,
        status.LastError,
        status.Interval);

    private async ValueTask<HackServerConnectionState> ConnectCoreAsync(
        Uri serverUrl, string login, string password, bool createAccount, CancellationToken cancellationToken)
    {
        _capabilities.Require(AppCapabilities.ServerConnectionManage);
        if (_services is null)
        {
            throw new HackServerConnectionException("This HackerOS host has no optional-server support.");
        }

        try
        {
            IServerConnectionService connection = _services.Connection();
            ServerState state = createAccount
                ? await connection.ConnectWithNewAccountAsync(serverUrl, login, password, cancellationToken).ConfigureAwait(false)
                : await connection.ConnectWithExistingAccountAsync(serverUrl, login, password, cancellationToken).ConfigureAwait(false);

            // ADR 0101 section 4: a (re)connection triggers a pass.
            _services.Scheduler?.Invoke().RequestSync();
            return ToPublic(state);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException)
        {
            throw new HackServerConnectionException($"Could not connect: {exception.Message}", exception);
        }
    }

    private static HackServerConnectionState ToPublic(ServerState? state) =>
        state is null
            ? HackServerConnectionState.NotConnected
            : new HackServerConnectionState(
                true,
                Uri.TryCreate(state.ServerBaseUrl, UriKind.Absolute, out Uri? url) ? url : null,
                state.AccountId,
                state.DeviceId);
}
