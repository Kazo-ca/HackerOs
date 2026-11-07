using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Keeps the server's <c>network.real.access</c> grants in line with the OS's approvals
/// (ADR 0233 §3): every app an administrator allowed gets its grant issued with the approved ports
/// of <c>[real-network-ports]</c>, and every app denied, blocked or pending again gets it withdrawn.
/// </summary>
public interface IRealNetworkGrantReconciler
{
    /// <summary>
    /// Issues or withdraws the grants of <paramref name="userId"/> on the connected server. Never
    /// throws: without a server it does nothing, and a failure is logged (<c>/var/log/syslog</c>)
    /// and retried at the next synchronization.
    /// </summary>
    /// <param name="userId">The OS user the grants apply to.</param>
    /// <param name="appId">Only this app, or <see langword="null"/> for every app.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    Task ReconcileAsync(string userId, string? appId = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IRealNetworkGrantReconciler"/>: reads the decisions with the same table as the
/// launch gate (<see cref="DangerousCapabilityEvaluator"/>) and calls <c>POST api/proxy/grants</c>
/// for each non-system app declaring <c>network.real.access</c>. It remembers what each server
/// already acknowledged, so a synchronization only sends what changed; the server ignores an
/// identical request anyway.
/// </summary>
/// <param name="catalog">The installed apps.</param>
/// <param name="permissions">The machine and user decisions.</param>
/// <param name="settings">Reads the <c>dangerousCapabilities</c> policy.</param>
/// <param name="connection">The server connection (base URL and token).</param>
/// <param name="proxy">The relay client.</param>
/// <param name="logger">Receives one warning per failed request.</param>
public sealed class RealNetworkGrantReconciler(
    AppCatalog catalog,
    IAppPermissionStore permissions,
    IOsSettingsService settings,
    IServerConnectionService connection,
    IProxyClient proxy,
    ILogger<RealNetworkGrantReconciler>? logger = null) : IRealNetworkGrantReconciler
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Dictionary<(string Server, string AppId, string UserId), string> _acknowledged = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async Task ReconcileAsync(string userId, string? appId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(userId, appId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or IOException or InvalidOperationException)
        {
            // The approval and the launch never wait on the server (ADR 0233 §3): the next sync retries.
            _logger.LogWarning(exception, "Real-network grants: the server could not be reached ({Reason}); retried at the next sync.", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileCoreAsync(string userId, string? appId, CancellationToken cancellationToken)
    {
        ServerConnectionState? state = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        string? accessToken = state is null ? null : await connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || accessToken is null)
        {
            return;
        }

        Uri server = new(state.ServerBaseUrl);
        DangerousCapabilityPolicy policy = await SecuritySettingsDocuments.ReadPolicyAsync(settings, cancellationToken).ConfigureAwait(false);
        AppPermissionDecisions decisions = await permissions.ReadAsync(cancellationToken).ConfigureAwait(false);

        foreach (AppManifest manifest in catalog.Manifests.Values.OrderBy(static manifest => manifest.Id, StringComparer.Ordinal))
        {
            if ((appId is not null && !string.Equals(manifest.Id, appId, StringComparison.Ordinal))
                || catalog.IsSystemApp(manifest.Id)
                || !manifest.Capabilities.Contains(AppCapabilities.NetworkRealAccess, StringComparer.Ordinal))
            {
                continue;
            }

            ProxyGrantIssueRequest request = RequestFor(manifest, userId, policy, decisions);
            (string, string, string) key = (state.ServerBaseUrl, manifest.Id, userId);
            string signature = Signature(request);
            if (_acknowledged.TryGetValue(key, out string? sent) && sent == signature)
            {
                continue;
            }

            try
            {
                await proxy.IssueGrantAsync(server, accessToken, request, cancellationToken).ConfigureAwait(false);
                _acknowledged[key] = signature;
            }
            catch (ServerConnectionException exception) when (exception.ProxyErrorCode == ProxyErrorCode.GrantIssuingDisabled)
            {
                // The operator closed the door (AcceptOsGrants: false): only GrantedApps counts there.
                _logger.LogInformation("Real-network grants: the server does not accept the grants the OS asks for (AcceptOsGrants).");
                return;
            }
            catch (NotSupportedException)
            {
                return;
            }
            catch (ServerConnectionException exception) when (exception.ProxyErrorCode is not null)
            {
                // A refusal of this one request (badly written port...): the others still go.
                _logger.LogWarning("Real-network grants: the server refused the grant of {AppId}: {Reason}", manifest.Id, exception.Message);
            }
        }
    }

    /// <summary>
    /// The request matching the decisions: the approved ports when the capability is granted, a
    /// withdrawal otherwise (denied by the administrator or the user, blocked by policy, or pending
    /// again because the app declares a port nobody approved yet).
    /// </summary>
    /// <param name="manifest">The app.</param>
    /// <param name="userId">The OS user.</param>
    /// <param name="policy">The <c>dangerousCapabilities</c> policy.</param>
    /// <param name="decisions">The machine and user decisions.</param>
    public static ProxyGrantIssueRequest RequestFor(
        AppManifest manifest, string userId, DangerousCapabilityPolicy policy, AppPermissionDecisions decisions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(decisions);
        bool granted = DangerousCapabilityEvaluator.Evaluate(manifest, false, policy, decisions)
            .Any(static status => status.Capability == AppCapabilities.NetworkRealAccess && status.IsGranted);
        IReadOnlyList<string> ports = granted
            ? [.. decisions.GetApprovedRealNetworkPorts(manifest.Id)
                .OrderBy(static port => port.Protocol, StringComparer.Ordinal)
                .ThenBy(static port => port.Port)
                .Select(static port => port.ToString())]
            : [];
        return new ProxyGrantIssueRequest(manifest.Id, userId, ports, Revoked: !granted);
    }

    private static string Signature(ProxyGrantIssueRequest request) =>
        (request.Revoked ? "revoked:" : "granted:") + string.Join(",", request.RealNetworkPorts ?? []);
}
