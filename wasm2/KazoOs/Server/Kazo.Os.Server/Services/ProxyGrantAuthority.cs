using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Kazo.Os.Server.Services;

/// <summary>
/// What the server grants one app of one account on the relay (ADR 0102 §4, ADR 0235 §3).
/// </summary>
/// <param name="AppId">The app.</param>
/// <param name="ApprovedPorts">
/// The real-network ports the app declared and an administrator approved (ADR 0235 §3), read from
/// the <c>realNetworkPorts</c> of its synchronized grant: the relay opens them on top of
/// <c>AllowedPorts</c>, unless <c>BlockedPorts</c> refuses them. Empty for an app granted by
/// <see cref="ProxyRelayPolicy.GrantedApps"/> only.
/// </param>
public sealed record ProxyAppGrant(string AppId, IReadOnlySet<RealNetworkPort> ApprovedPorts)
{
    /// <summary>Whether <paramref name="port"/> of <paramref name="protocol"/> is approved for the app.</summary>
    /// <param name="protocol"><c>tcp</c> or <c>udp</c>.</param>
    /// <param name="port">Target port.</param>
    public bool Approves(string protocol, int port) => ApprovedPorts.Contains(new RealNetworkPort(protocol, port));
}

/// <summary>
/// Decides, on the server's own data, whether an app may use the relay (ADR 0102 §4). The OS
/// checks <c>network.real.access</c> first; this second check stops a modified client from
/// skipping it, since it never reads what the client says about its grants.
/// </summary>
public interface IProxyGrantAuthority
{
    /// <summary>
    /// The account's grant of <c>network.real.access</c> to <paramref name="appId"/> on the server,
    /// with its approved ports, or <see langword="null"/> when the app holds none.
    /// </summary>
    /// <param name="accountId">The authenticated account.</param>
    /// <param name="appId">The app the request is made for.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<ProxyAppGrant?> GetGrantAsync(Guid accountId, string appId, CancellationToken cancellationToken);

    /// <summary>
    /// Every app of the account holding a synchronized <c>network.real.access</c> grant that declares
    /// ports, for <c>GET /api/proxy/policy</c> (Paramètres shows each app's ports, ADR 0235 §3).
    /// </summary>
    /// <param name="accountId">The authenticated account.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlyList<ProxyAppGrant>> ListGrantsWithPortsAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Grants the relay to the apps of <see cref="ProxyRelayPolicy.GrantedApps"/> (by default the
/// system commands that declare <c>network.real.access</c>), and to an app named by a non-revoked
/// <c>network.real.access</c> record in the account's synchronized grants (ADR 0031: grants are
/// server-authoritative and the client never pushes them). Such a record may carry the app's
/// approved real-network ports (<c>realNetworkPorts</c>, ADR 0235 §3). The server writes those records
/// itself when the OS asks (<see cref="ProxyGrantIssuer"/>, ADR 0233); with
/// <see cref="ProxyRelayPolicy.AcceptOsGrants"/> off they are ignored.
/// </summary>
/// <param name="policy">The effective relay policy.</param>
/// <param name="db">The server database holding the synchronized grants.</param>
public sealed class ProxyGrantAuthority(ProxyRelayPolicy policy, KazoOsServerDbContext db) : IProxyGrantAuthority
{
    /// <summary>The capability the relay requires.</summary>
    public const string RealNetworkCapability = "network.real.access";

    private static readonly IReadOnlySet<RealNetworkPort> NoPorts = new HashSet<RealNetworkPort>();

    /// <inheritdoc />
    public async Task<ProxyAppGrant?> GetGrantAsync(Guid accountId, string appId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProxyAppGrant> synchronized = await ReadSynchronizedGrantsAsync(accountId, cancellationToken).ConfigureAwait(false);
        ProxyAppGrant? record = synchronized.FirstOrDefault(grant => grant.AppId == appId);
        if (record is not null)
        {
            return record;
        }

        return policy.GrantedApps.Contains(appId, StringComparer.Ordinal) ? new ProxyAppGrant(appId, NoPorts) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProxyAppGrant>> ListGrantsWithPortsAsync(Guid accountId, CancellationToken cancellationToken) =>
        [.. (await ReadSynchronizedGrantsAsync(accountId, cancellationToken).ConfigureAwait(false))
            .Where(static grant => grant.ApprovedPorts.Count > 0)
            .OrderBy(static grant => grant.AppId, StringComparer.Ordinal)];

    /// <summary>
    /// The non-revoked <c>network.real.access</c> grants of the account, one per app. The latest
    /// revision of each grant record decides: a revocation is a newer revision with
    /// <c>isRevoked</c> set (grants have no tombstones, ADR 0025). Two live records for one app
    /// (two devices, two approvals) merge their ports.
    /// </summary>
    private async Task<IReadOnlyList<ProxyAppGrant>> ReadSynchronizedGrantsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!policy.AcceptOsGrants)
        {
            // ADR 0233 §2: the operator closed the door; only GrantedApps counts.
            return [];
        }

        List<SyncRecordEntity> records = await db.SyncRecords.AsNoTracking()
            .Where(record => record.OwnerAccountId == accountId && record.Domain == SyncDomain.Grants && !record.IsTombstone)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. records
            .GroupBy(static record => record.RecordId)
            .Select(static group => group.MaxBy(static record => record.Revision)!)
            .Select(static record => Read(record.PayloadJson))
            .OfType<ProxyAppGrant>()
            .GroupBy(static grant => grant.AppId, StringComparer.Ordinal)
            .Select(static group => new ProxyAppGrant(
                group.Key,
                group.SelectMany(static grant => grant.ApprovedPorts).ToHashSet()))];
    }

    /// <summary>
    /// Reads a grant payload (camelCase <c>GrantsSyncPayload</c>): the app and its approved ports
    /// when it grants the relay and is not revoked, else <see langword="null"/>. An invalid port
    /// entry is dropped, never widened into something else.
    /// </summary>
    private static ProxyAppGrant? Read(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "appId") is not { Length: > 0 } appId
                || Text(root, "capability") != RealNetworkCapability
                || (root.TryGetProperty("isRevoked", out JsonElement revoked) && revoked.ValueKind == JsonValueKind.True))
            {
                return null;
            }

            IEnumerable<string> declared = root.TryGetProperty("realNetworkPorts", out JsonElement ports) && ports.ValueKind == JsonValueKind.Array
                ? ports.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!)
                : [];
            return new ProxyAppGrant(appId, RealNetworkPorts.ParseValid(declared).ToHashSet());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
