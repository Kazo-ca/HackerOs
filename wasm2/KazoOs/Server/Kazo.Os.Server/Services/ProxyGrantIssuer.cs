using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Kazo.Os.Simulation.Abstractions.Sync;
using Microsoft.EntityFrameworkCore;

namespace Kazo.Os.Server.Services;

/// <summary>
/// Issues the <c>network.real.access</c> grant an OS administrator approved, with its ports
/// (<c>POST /api/proxy/grants</c>, ADR 0233 §1). The OS never pushes a grant through sync
/// (ADR 0031): it asks, and the server checks the shape and writes the grant record itself.
/// </summary>
public interface IProxyGrantIssuer
{
    /// <summary>Issues or withdraws the grant of <paramref name="request"/> for the account.</summary>
    /// <param name="accountId">The authenticated account.</param>
    /// <param name="deviceId">The authenticated device (checked against the account).</param>
    /// <param name="request">The app, user, approved ports and whether to withdraw.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ProxyRequestException">The request is refused (shape, device, option off).</exception>
    Task<ProxyGrantIssued> IssueAsync(Guid accountId, Guid deviceId, ProxyGrantIssueRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Writes the grant as a revision of one stable <c>grants</c> sync record per account, app and user,
/// so a new approval replaces the previous one and the OS pulls it back like any other grant. A
/// request equal to the latest revision writes nothing. A port of <c>BlockedPorts</c> is accepted
/// here and still refused on use (ADR 0233 §1), so the policy can change without rewriting grants.
/// </summary>
/// <param name="policy">The effective relay policy (<see cref="ProxyRelayPolicy.AcceptOsGrants"/>).</param>
/// <param name="db">The server database holding the synchronized grants.</param>
/// <param name="audit">Records <c>PROXY_GRANT_ISSUE</c> and <c>PROXY_GRANT_REVOKE</c>.</param>
public sealed class ProxyGrantIssuer(ProxyRelayPolicy policy, KazoOsServerDbContext db, IAuditService audit) : IProxyGrantIssuer
{
    /// <summary>The most ports one grant may approve.</summary>
    public const int MaxPorts = 64;

    /// <summary>The most characters of an app or user id.</summary>
    public const int MaxIdLength = 256;

    /// <summary>The grant's source, as the OS's <c>CapabilityGrantSource</c> names it.</summary>
    public const string Source = "AdministratorApproval";

    /// <inheritdoc />
    public async Task<ProxyGrantIssued> IssueAsync(
        Guid accountId, Guid deviceId, ProxyGrantIssueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!policy.AcceptOsGrants)
        {
            throw new ProxyRequestException(ProxyErrorCode.GrantIssuingDisabled,
                "The operator turned off the grants the OS asks for (KazoOs:Proxy:AcceptOsGrants).");
        }

        IReadOnlyList<string> ports = Validate(request);
        await EnsureDeviceAsync(accountId, deviceId, cancellationToken).ConfigureAwait(false);

        Guid recordId = RecordIdOf(accountId, request.AppId, request.UserId);
        string payload = JsonSerializer.Serialize(
            new GrantsSyncPayload(request.AppId, request.UserId, ProxyGrantAuthority.RealNetworkCapability, Source, [], request.Revoked)
            {
                RealNetworkPorts = ports.Count == 0 ? null : ports
            },
            GrantsSyncContractsJsonContext.Default.GrantsSyncPayload);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        SyncRecordEntity? latest = await db.SyncRecords.AsNoTracking()
            .Where(record => record.RecordId == recordId && record.Domain == SyncDomain.Grants && record.OwnerAccountId == accountId)
            .OrderByDescending(record => record.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is not null && latest.ContentHash == hash)
        {
            return new ProxyGrantIssued(request.AppId, request.UserId, recordId, latest.Revision, Changed: false, ports, request.Revoked);
        }

        if (latest is null && request.Revoked)
        {
            // Nothing to withdraw: a first revision that is already revoked would only add noise.
            return new ProxyGrantIssued(request.AppId, request.UserId, recordId, 0, Changed: false, ports, Revoked: true);
        }

        long revision = (latest?.Revision ?? 0) + 1;
        long sequence = (await db.SyncRecords.MaxAsync(record => (long?)record.ServerSequence, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.SyncRecords.Add(new SyncRecordEntity
        {
            RecordId = recordId,
            Domain = SyncDomain.Grants,
            SchemaVersion = 1,
            OwnerAccountId = accountId,
            OriginDeviceId = deviceId,
            Revision = revision,
            ModifiedUtc = now,
            ServerReceivedUtc = now,
            ServerSequence = sequence,
            ContentHash = hash,
            IsTombstone = false,
            PayloadJson = payload
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await audit.RecordAsync(accountId, deviceId, request.Revoked ? "PROXY_GRANT_REVOKE" : "PROXY_GRANT_ISSUE",
            JsonSerializer.Serialize(new GrantAuditDetails(request.AppId, request.UserId, ports, revision), GrantAuditJsonContext.Default.GrantAuditDetails),
            cancellationToken).ConfigureAwait(false);

        return new ProxyGrantIssued(request.AppId, request.UserId, recordId, revision, Changed: true, ports, request.Revoked);
    }

    /// <summary>
    /// The stable record id of the grant of <paramref name="appId"/> to <paramref name="userId"/> in
    /// the account: the first 16 bytes of a SHA-256 of the three, so every approval of the same app
    /// for the same user is a new revision of the same record.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="appId">The app.</param>
    /// <param name="userId">The OS user.</param>
    public static Guid RecordIdOf(Guid accountId, string appId, string userId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"kazoos.proxy.grant\n{accountId:N}\n{appId}\n{userId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Checks the shape (ADR 0233 §1) and returns the ports in canonical order.</summary>
    private static IReadOnlyList<string> Validate(ProxyGrantIssueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AppId) || request.AppId.Length > MaxIdLength)
        {
            throw new ProxyRequestException(ProxyErrorCode.CapabilityDenied, "A valid registered application identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(request.UserId) || request.UserId.Length > MaxIdLength)
        {
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "A valid user identifier is required.");
        }

        IReadOnlyList<string> declared = request.RealNetworkPorts ?? [];
        if (declared.Count > MaxPorts)
        {
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, $"A grant approves at most {MaxPorts} ports.");
        }

        HashSet<RealNetworkPort> seen = [];
        foreach (string value in declared)
        {
            if (!RealNetworkPorts.TryParse(value, out RealNetworkPort port, out string? error))
            {
                throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, error!);
            }

            if (!seen.Add(port))
            {
                throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, $"Real-network port '{value}' is listed twice.");
            }
        }

        return RealNetworkPorts.Normalize(declared) ?? [];
    }

    private async Task EnsureDeviceAsync(Guid accountId, Guid deviceId, CancellationToken cancellationToken)
    {
        bool authorized = await db.Devices.AsNoTracking().AnyAsync(
            device => device.DeviceId == deviceId && device.AccountId == accountId && !device.IsRevoked,
            cancellationToken).ConfigureAwait(false);
        if (!authorized)
        {
            throw new ProxyRequestException(ProxyErrorCode.CapabilityDenied,
                "The authenticated device is not registered to this account or has been revoked.");
        }
    }
}

/// <summary>The details of a grant audit entry.</summary>
/// <param name="AppId">The app.</param>
/// <param name="UserId">The OS user.</param>
/// <param name="Ports">The approved ports.</param>
/// <param name="Revision">The record revision written.</param>
internal sealed record GrantAuditDetails(string AppId, string UserId, IReadOnlyList<string> Ports, long Revision);

/// <summary>Source-generated JSON for <see cref="GrantAuditDetails"/>: values are escaped, never concatenated.</summary>
[JsonSerializable(typeof(GrantAuditDetails))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GrantAuditJsonContext : JsonSerializerContext { }
