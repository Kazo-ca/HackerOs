using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Connects this device to the optional Kazo OS server and synchronizes it (ADR 0056,
/// section 10), as Settings &gt; Server Connection does. Replaces <c>IServerConnectionService</c>
/// and the sync services, which apps may no longer inject (ADR 0055).
/// </summary>
/// <remarks>
/// <para>
/// Every member requires the dangerous capability <c>server.connection.manage</c>; a missing one
/// raises <see cref="KazoUnauthorizedAccessException"/>.
/// </para>
/// <para>
/// The server is optional (<c>AGENTS.md</c>): without one, <see cref="GetStateAsync(CancellationToken)"/> reports
/// <see cref="KazoServerConnectionState.NotConnected"/> and <see cref="SyncAsync(KazoSyncDomain, KazoSyncDirection, CancellationToken)"/>
/// does nothing. Only a request the user made (connect, create an account, synchronize) raises
/// <see cref="KazoServerConnectionException"/> when the server refuses it or cannot be reached.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await KazoServerConnection.ConnectAsync(new Uri("https://kazoos.example"), "benoit", password);
/// await KazoServerConnection.SyncAsync(KazoSyncDomain.FileSystem, KazoSyncDirection.Push);
/// </code>
/// </example>
public static class KazoServerConnection
{
    /// <summary>Gets the current connection.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<KazoServerConnectionState> GetStateAsync(CancellationToken cancellationToken = default) =>
        GetStateAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Gets the current connection through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<KazoServerConnectionState> GetStateAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetStateAsync(cancellationToken));

    /// <summary>Links this device to an existing server account.</summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="login">Server account name.</param>
    /// <param name="password">Server account password.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoServerConnectionException">The server refused the credentials or could not be reached.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoServerConnectionState> ConnectAsync(
        Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
        ConnectAsync(KazoExecutionContext.Current, serverUrl, login, password, cancellationToken);

    /// <summary>Links this device to an existing server account through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="login">Server account name.</param>
    /// <param name="password">Server account password.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<KazoServerConnectionState> ConnectAsync(
        IAppExecutionContext context, Uri serverUrl, string login, string password, CancellationToken cancellationToken = default)
    {
        ValidateCredentials(serverUrl, login, password);
        return Call(context, gateway => gateway.ConnectAsync(serverUrl, login, password, cancellationToken));
    }

    /// <summary>Creates a server account and links this device to it.</summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="login">New account name.</param>
    /// <param name="password">New account password.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoServerConnectionException">The server refused the account or could not be reached.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoServerConnectionState> CreateAccountAsync(
        Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
        CreateAccountAsync(KazoExecutionContext.Current, serverUrl, login, password, cancellationToken);

    /// <summary>Creates a server account through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="login">New account name.</param>
    /// <param name="password">New account password.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<KazoServerConnectionState> CreateAccountAsync(
        IAppExecutionContext context, Uri serverUrl, string login, string password, CancellationToken cancellationToken = default)
    {
        ValidateCredentials(serverUrl, login, password);
        return Call(context, gateway => gateway.CreateAccountAsync(serverUrl, login, password, cancellationToken));
    }

    /// <summary>
    /// Links this device to a server account with a one-time pairing code (ADR 0191 §6), printed by
    /// <c>Kazo.Os.Server pair &lt;account&gt;</c> on the server's machine.
    /// </summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="code">The pairing code, for example <c>H9R4-JYYG</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoServerConnectionException">The server refused the code or could not be reached.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoServerConnectionState> PairAsync(Uri serverUrl, string code, CancellationToken cancellationToken = default) =>
        PairAsync(KazoExecutionContext.Current, serverUrl, code, cancellationToken);

    /// <summary>Links this device with a pairing code through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="code">The pairing code.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<KazoServerConnectionState> PairAsync(
        IAppExecutionContext context, Uri serverUrl, string code, CancellationToken cancellationToken = default)
    {
        ValidateServerUrl(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Call(context, gateway => gateway.PairAsync(serverUrl, code, cancellationToken));
    }

    /// <summary>
    /// Asks an address whether it is a Kazo OS server, without linking this device (the
    /// "Test" button of Settings). Never throws for an unreachable address: the result says why.
    /// </summary>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoServerProbe> TestAsync(Uri serverUrl, CancellationToken cancellationToken = default) =>
        TestAsync(KazoExecutionContext.Current, serverUrl, cancellationToken);

    /// <summary>Tests an address through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="serverUrl">Absolute server base URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<KazoServerProbe> TestAsync(IAppExecutionContext context, Uri serverUrl, CancellationToken cancellationToken = default)
    {
        ValidateServerUrl(serverUrl);
        return Call(context, gateway => gateway.TestAsync(serverUrl, cancellationToken));
    }

    /// <summary>Unlinks this device from its server account.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        DisconnectAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Unlinks this device through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task DisconnectAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, async gateway =>
        {
            await gateway.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>Runs one synchronization pass; does nothing when the device is not connected.</summary>
    /// <param name="domain">What to synchronize.</param>
    /// <param name="direction">Push or pull. <see cref="KazoSyncDomain.Grants"/> only pulls.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <exception cref="ArgumentException"><see cref="KazoSyncDirection.Push"/> was asked for <see cref="KazoSyncDomain.Grants"/>.</exception>
    /// <exception cref="KazoServerConnectionException">The server refused the pass or could not be reached.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task SyncAsync(KazoSyncDomain domain, KazoSyncDirection direction, CancellationToken cancellationToken = default) =>
        SyncAsync(KazoExecutionContext.Current, domain, direction, cancellationToken);

    /// <summary>Runs one synchronization pass through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="domain">What to synchronize.</param>
    /// <param name="direction">Push or pull.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public static Task SyncAsync(
        IAppExecutionContext context, KazoSyncDomain domain, KazoSyncDirection direction, CancellationToken cancellationToken = default)
    {
        if (domain == KazoSyncDomain.Grants && direction == KazoSyncDirection.Push)
        {
            throw new ArgumentException("Grants are issued by the server: this domain can only be pulled (ADR 0031).", nameof(direction));
        }

        return Call(context, async gateway =>
        {
            await gateway.SyncAsync(domain, direction, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Gets the pending synchronization conflicts of the session user (ADR 0101).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<IReadOnlyList<KazoSyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default) =>
        GetConflictsAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Gets the unmerged records through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<IReadOnlyList<KazoSyncConflict>> GetConflictsAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetConflictsAsync(cancellationToken));

    /// <summary>
    /// Gets the network relay policy of the connected server (ADR 0102 §5): which ports and
    /// networks apps holding <c>network.real.access</c> can reach through it, and its quotas.
    /// </summary>
    /// <returns>The policy, or <see langword="null"/> when the device is not connected.</returns>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoServerConnectionException">The server could not be reached.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoServerRelayPolicy?> GetRelayPolicyAsync(CancellationToken cancellationToken = default) =>
        GetRelayPolicyAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Gets the relay policy through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<KazoServerRelayPolicy?> GetRelayPolicyAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetRelayPolicyAsync(cancellationToken));

    /// <summary>Opens the OS conflict dialog, where the user picks a version for each conflict (ADR 0101).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task ShowConflictsAsync(CancellationToken cancellationToken = default) =>
        ShowConflictsAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Opens the OS conflict dialog through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task ShowConflictsAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, async gateway =>
        {
            await gateway.ShowConflictsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>Gets the state of the periodic synchronization (ADR 0101 section 4).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default) =>
        GetSyncStatusAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Gets the state of the periodic synchronization through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<KazoSyncStatus> GetSyncStatusAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetSyncStatusAsync(cancellationToken));

    /// <summary>Pauses or resumes the session user's periodic synchronization (<c>~/.config/kazoos/sync.config</c>).</summary>
    /// <param name="paused">Whether to pause.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task SetSyncPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        SetSyncPausedAsync(KazoExecutionContext.Current, paused, cancellationToken);

    /// <summary>Pauses or resumes the periodic synchronization through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="paused">Whether to pause.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task SetSyncPausedAsync(IAppExecutionContext context, bool paused, CancellationToken cancellationToken = default) =>
        Call(context, async gateway =>
        {
            await gateway.SetSyncPausedAsync(paused, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>Runs one complete synchronization pass, every domain in order; does nothing when not connected.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The status after the pass.</returns>
    /// <exception cref="KazoServerConnectionException">The server refused the pass or could not be reached.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>server.connection.manage</c>.</exception>
    public static Task<KazoSyncStatus> SyncNowAsync(CancellationToken cancellationToken = default) =>
        SyncNowAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Runs one complete synchronization pass through an explicit context.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public static Task<KazoSyncStatus> SyncNowAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.SyncNowAsync(cancellationToken));

    private static void ValidateCredentials(Uri serverUrl, string login, string password)
    {
        ValidateServerUrl(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentException.ThrowIfNullOrEmpty(password);
    }

    private static void ValidateServerUrl(Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (!serverUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The server URL must be absolute.", nameof(serverUrl));
        }
    }

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppServerConnectionGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.ServerConnection).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }
}
