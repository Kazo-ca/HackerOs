using System.Security.Cryptography;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// Pushes/pulls the single <see cref="FileAssociationSettingsDocuments"/> file
/// (<c>/etc/hackeros/file-associations.json</c>) to/from the optional server's FileAssociations sync
/// domain (ADR 0033). Since ADR 0044 the document is an ordinary system file; this service reads and
/// writes it as an audited system operation. A no-op when this device isn't connected. Deleting the
/// file pushes a tombstone; a received tombstone restores the installation defaults (ADR 0101).
/// </summary>
public interface IFileAssociationsSyncService
{
    /// <summary>Pushes the file-association document if it changed since the last successful push.</summary>
    Task PushAsync(CancellationToken cancellationToken = default);

    /// <summary>Pulls and applies the remote file-association document if it changed since the last pull.</summary>
    Task PullAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IFileAssociationsSyncService"/> implementation.</summary>
public sealed class FileAssociationsSyncService(
    IOsSettingsService settings,
    IServerConnectionService connection,
    ISyncClient syncClient,
    ISyncCursorRepository cursors,
    ISyncRecordStateRepository recordState) : IFileAssociationsSyncService
{
    private static readonly Guid RecordId = ComputeRecordId(FileAssociationSettingsDocuments.Key);

    /// <inheritdoc />
    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        ServerConnectionState? state = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        string? accessToken = state is null ? null : await connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || accessToken is null)
        {
            return;
        }

        string? content = await settings.ReadFileAsync(FileAssociationSettingsDocuments.File, cancellationToken).ConfigureAwait(false);
        SyncRecordTrackingState? tracked = await recordState.GetAsync(
            SyncDomain.FileAssociations, RecordId, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            // ADR 0101 section 1: deleting the synchronized file is a deletion for the other devices
            // too. A file this device never synchronized leaves no tombstone.
            if (tracked is { IsTombstone: false })
            {
                await PushTombstoneAsync(state, accessToken, tracked, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string contentHash = ComputeHash(content);
        if (tracked is { IsTombstone: false } && string.Equals(tracked.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return; // Unchanged since the last successful push/pull.
        }

        long nextRevision = (tracked?.Revision ?? 0) + 1;
        SyncRecordEnvelope envelope = new(
            RecordId,
            SyncDomain.FileAssociations,
            SchemaVersion: 1,
            state.AccountId,
            state.DeviceId,
            nextRevision,
            DateTimeOffset.UtcNow,
            ServerReceivedUtc: null,
            contentHash,
            IsTombstone: false,
            content);

        PushResponse response = await syncClient.PushAsync(
            new Uri(state.ServerBaseUrl),
            accessToken,
            new PushRequest(SyncDomain.FileAssociations, [envelope], Guid.NewGuid()),
            cancellationToken).ConfigureAwait(false);

        if (response.Conflicts.Count > 0)
        {
            // Same as Settings (ADR 0029 Decision 6): a conflicted push is resolved by pulling and
            // applying the server's current copy instead — handled by PullAsync, not retried here.
            return;
        }

        await recordState.SetAsync(
            SyncDomain.FileAssociations, RecordId, envelope.Revision, envelope.ContentHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PullAsync(CancellationToken cancellationToken = default)
    {
        ServerConnectionState? state = await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        string? accessToken = state is null ? null : await connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || accessToken is null)
        {
            return;
        }

        Uri serverBaseUrl = new(state.ServerBaseUrl);
        string? cursor = await cursors.GetCursorAsync(SyncDomain.FileAssociations, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            PullResponse response = await syncClient.PullAsync(
                serverBaseUrl, accessToken, new PullRequest(SyncDomain.FileAssociations, cursor, MaxRecords: 100), cancellationToken)
                .ConfigureAwait(false);

            foreach (SyncRecordEnvelope envelope in response.Records)
            {
                if (envelope.RecordId != RecordId)
                {
                    continue; // Not our one document (shouldn't happen for this domain).
                }

                SyncRecordTrackingState? tracked = await recordState.GetAsync(
                    SyncDomain.FileAssociations, RecordId, cancellationToken).ConfigureAwait(false);
                if (tracked is not null && envelope.Revision <= tracked.Revision)
                {
                    continue; // Already applied, or this device's own push coming back.
                }

                if (envelope.IsTombstone || envelope.PayloadJson is null)
                {
                    // Deleted on another device: back to the installation defaults, like a fresh
                    // machine, rather than leaving the OS without the file.
                    await settings.WriteFileAsync(
                        FileAssociationSettingsDocuments.File, FileAssociationSettingsDocuments.DefaultContent, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await recordState.SetAsync(
                        SyncDomain.FileAssociations,
                        RecordId,
                        new SyncRecordTrackingState(envelope.Revision, ComputeHash(FileAssociationSettingsDocuments.DefaultContent)),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (new FileAssociationSettingsValidator().Validate(envelope.PayloadJson).Count != 0)
                {
                    continue; // Never write an invalid remote copy over the local file.
                }

                await settings.WriteFileAsync(FileAssociationSettingsDocuments.File, envelope.PayloadJson, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                await recordState.SetAsync(
                    SyncDomain.FileAssociations, envelope.RecordId, envelope.Revision, envelope.ContentHash, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (response.NextCursor is not null)
            {
                cursor = response.NextCursor;
                await cursors.SetCursorAsync(SyncDomain.FileAssociations, cursor, cancellationToken).ConfigureAwait(false);
            }

            if (!response.HasMore)
            {
                break;
            }
        }
    }

    private async Task PushTombstoneAsync(
        ServerConnectionState state, string accessToken, SyncRecordTrackingState tracked, CancellationToken cancellationToken)
    {
        long revision = tracked.Revision + 1;
        string hash = ComputeHash(string.Empty);
        SyncRecordEnvelope tombstone = new(
            RecordId, SyncDomain.FileAssociations, SchemaVersion: 1, state.AccountId, state.DeviceId,
            revision, DateTimeOffset.UtcNow, ServerReceivedUtc: null, hash, IsTombstone: true, PayloadJson: null);
        PushResponse response = await syncClient.PushAsync(
            new Uri(state.ServerBaseUrl), accessToken, new PushRequest(SyncDomain.FileAssociations, [tombstone], Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
        if (response.Conflicts.Count == 0)
        {
            await recordState.SetAsync(
                SyncDomain.FileAssociations, RecordId, new SyncRecordTrackingState(revision, hash, IsTombstone: true), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Guid ComputeRecordId(SettingsDocumentKey key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
