using System.Text.Json;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Kazo.Os.Server.Services;

/// <summary>
/// Decides, on the server's own data, whether an app may use the relay (ADR 0102 §4). The OS
/// checks <c>network.real.access</c> first; this second check stops a modified client from
/// skipping it, since it never reads what the client says about its grants.
/// </summary>
public interface IProxyGrantAuthority
{
    /// <summary>Whether the account's <paramref name="appId"/> holds <c>network.real.access</c> on the server.</summary>
    /// <param name="accountId">The authenticated account.</param>
    /// <param name="appId">The app the request is made for.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<bool> IsGrantedAsync(Guid accountId, string appId, CancellationToken cancellationToken);
}

/// <summary>
/// Grants the relay to the apps of <see cref="ProxyRelayPolicy.GrantedApps"/> (by default the
/// system commands that declare <c>network.real.access</c>), and to an app named by a non-revoked
/// <c>network.real.access</c> record in the account's synchronized grants (ADR 0031: grants are
/// server-authoritative and the client never pushes them).
/// </summary>
/// <param name="policy">The effective relay policy.</param>
/// <param name="db">The server database holding the synchronized grants.</param>
public sealed class ProxyGrantAuthority(ProxyRelayPolicy policy, KazoOsServerDbContext db) : IProxyGrantAuthority
{
    /// <summary>The capability the relay requires.</summary>
    public const string RealNetworkCapability = "network.real.access";

    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(Guid accountId, string appId, CancellationToken cancellationToken)
    {
        if (policy.GrantedApps.Contains(appId, StringComparer.Ordinal))
        {
            return true;
        }

        // The latest revision of each grant record decides: a revocation is a newer revision with
        // isRevoked set (grants have no tombstones, ADR 0025).
        List<SyncRecordEntity> records = await db.SyncRecords.AsNoTracking()
            .Where(record => record.OwnerAccountId == accountId && record.Domain == SyncDomain.Grants && !record.IsTombstone)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records
            .GroupBy(static record => record.RecordId)
            .Select(static group => group.MaxBy(static record => record.Revision)!)
            .Any(record => Grants(record.PayloadJson, appId));
    }

    /// <summary>Whether a grant payload (camelCase <c>GrantsSyncPayload</c>) grants the relay to <paramref name="appId"/>.</summary>
    private static bool Grants(string? payloadJson, string appId)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "appId") == appId
                && Text(root, "capability") == RealNetworkCapability
                && !(root.TryGetProperty("isRevoked", out JsonElement revoked) && revoked.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
