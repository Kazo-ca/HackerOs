using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Pulls server-issued capability grants into the durable local grant store (ADR 0031). Pull-only —
/// there is no <c>PushAsync</c>: nothing in this codebase today legitimately originates a client-side
/// grant to push, and the server does not validate pushed Grants payload semantics, so the client
/// simply never pushes rather than trusting an unvalidated server not to accept a crafted widening
/// push. A no-op when this device isn't connected or no server has ever issued a grant record yet.
/// </summary>
public interface IGrantsSyncService
{
    /// <summary>Pulls and durably applies every remote Grants change since the last pull.</summary>
    Task PullAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IGrantsSyncService"/> implementation.</summary>
public sealed class GrantsSyncService(
    IPersistentCapabilityGrantRepository grants,
    IServerConnectionService connection,
    ISyncClient syncClient,
    ISyncCursorRepository cursors) : IGrantsSyncService
{
    /// <summary>
    /// Issues or withdraws the session user's <c>network.real.access</c> grants on the server before
    /// each pull (ADR 0233 §3), so the pull brings them back; <see langword="null"/> skips it.
    /// </summary>
    public IRealNetworkGrantReconciler? RealNetworkGrants { get; init; }

    /// <summary>Gives the session user the reconciliation runs for; <see langword="null"/> without a session.</summary>
    public Func<string?>? CurrentUserId { get; init; }

    /// <inheritdoc />
    public async Task PullAsync(CancellationToken cancellationToken = default)
    {
        if (RealNetworkGrants is not null && CurrentUserId?.Invoke() is { Length: > 0 } userId)
        {
            await RealNetworkGrants.ReconcileAsync(userId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        ServerConnectionState? state = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        string? accessToken = state is null ? null : await connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || accessToken is null)
        {
            return;
        }

        Uri serverBaseUrl = new(state.ServerBaseUrl);
        string? cursor = await cursors.GetCursorAsync(SyncDomain.Grants, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            PullResponse response = await syncClient.PullAsync(
                serverBaseUrl, accessToken, new PullRequest(SyncDomain.Grants, cursor, MaxRecords: 100), cancellationToken)
                .ConfigureAwait(false);

            foreach (SyncRecordEnvelope envelope in response.Records)
            {
                if (envelope.PayloadJson is null)
                {
                    continue; // Tombstones are blocked server-side for this domain (ADR 0025) — nothing to apply.
                }

                GrantsSyncPayload payload = JsonSerializer.Deserialize(
                    envelope.PayloadJson, GrantsSyncContractsJsonContext.Default.GrantsSyncPayload)!;

                // ImportAsync upserts by id, so redelivering the same RecordId (the at-least-once pull
                // design, or a later revocation of the same grant) updates the same row rather than
                // duplicating it — no separate change-tracking against ISyncRecordStateRepository needed
                // for a pull-only domain with nothing to diff a push against.
                await grants.ImportAsync(
                    CapabilityGrantId.FromGuid(envelope.RecordId),
                    payload.AppId,
                    payload.UserId,
                    payload.Capability,
                    Enum.Parse<CapabilityGrantSource>(payload.Source),
                    payload.Constraints.Select(ToConstraint),
                    payload.IsRevoked,
                    AppAuthority.System,
                    cancellationToken).ConfigureAwait(false);
            }

            // The server only returns NextCursor when HasMore is true (the same at-least-once design
            // Settings/FileSystem sync already rely on) — only persist a cursor advance when given one.
            if (response.NextCursor is not null)
            {
                cursor = response.NextCursor;
                await cursors.SetCursorAsync(SyncDomain.Grants, cursor, cancellationToken).ConfigureAwait(false);
            }

            if (!response.HasMore)
            {
                break;
            }
        }
    }

    private static CapabilityConstraint ToConstraint(GrantConstraintPayload payload) =>
        Enum.Parse<CapabilityConstraintKind>(payload.Kind) switch
        {
            CapabilityConstraintKind.VirtualPath => new VirtualPathCapabilityConstraint(
                VirtualPath.Parse(payload.PathValue!), payload.IncludeDescendants!.Value),
            CapabilityConstraintKind.NetworkHost => new NetworkHostCapabilityConstraint(payload.Host!),
            CapabilityConstraintKind.NetworkPort => new NetworkPortCapabilityConstraint(
                (ushort)payload.MinPort!.Value, (ushort)payload.MaxPort!.Value),
            var kind => throw new InvalidDataException($"Unknown capability constraint kind '{kind}'.")
        };
}
