using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>What one sync pass moved, for <c>/var/log/syslog</c> and the Settings page (ADR 0101 section 4).</summary>
/// <param name="Sent">Records pushed and accepted (changes and deletions).</param>
/// <param name="Received">Remote records applied locally (changes and deletions).</param>
/// <param name="Conflicts">Conflicts found by this pass.</param>
public sealed record SyncPassResult(int Sent, int Received, int Conflicts)
{
    /// <summary>A pass that did nothing (not connected, no session, nothing changed).</summary>
    public static SyncPassResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// Pushes/pulls filesystem entries under the active user's <c>/home/{userId}</c> to/from the
/// optional server's FileSystem sync domain (ADR 0030, ADR 0101). A no-op when this device isn't
/// connected or no session is active. Since ADR 0044 this is also how user settings
/// (<c>~/.config/&lt;appId&gt;/</c>) travel; device-local state under <c>~/.local/state/</c>
/// (window geometry, migration markers, pending conflicts) is never pushed nor applied.
/// </summary>
/// <remarks>
/// <para>
/// Deletions travel as tombstones (ADR 0101 section 1): an entry this device already synchronized
/// and that disappeared from the home is pushed with <c>IsTombstone</c>; a received tombstone moves
/// the local entry to the user's trash, marked "deleted on another device", so it can be restored.
/// That trashed copy is never pushed back.
/// </para>
/// <para>
/// A file changed on both sides is a conflict (ADR 0101 section 2): it is frozen (kept as is, neither
/// sent nor replaced) until the user picks a version with <see cref="ResolveConflictAsync"/>. Pending
/// conflicts live in <c>~/.local/state/hackeros/sync-conflicts.json</c>.
/// </para>
/// </remarks>
public interface IFileSystemSyncService
{
    /// <summary>Raised when the pending conflicts of the session user change.</summary>
    event EventHandler? ConflictsChanged;

    /// <summary>Pushes every changed or deleted entry under the home directory since its last successful push.</summary>
    Task<SyncPassResult> PushAsync(CancellationToken cancellationToken = default);

    /// <summary>Pulls and applies every remote FileSystem change since the last pull.</summary>
    Task<SyncPassResult> PullAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the pending conflicts of the session user, oldest first; empty without a session.</summary>
    ValueTask<IReadOnlyList<SyncFileConflict>> GetConflictsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the start of both versions of a text file in conflict.</summary>
    /// <exception cref="KeyNotFoundException">No pending conflict has this record id.</exception>
    ValueTask<SyncConflictPreview> GetConflictPreviewAsync(Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>Applies the user's choice to one pending conflict and unfreezes the file.</summary>
    /// <exception cref="KeyNotFoundException">No pending conflict has this record id.</exception>
    /// <exception cref="InvalidOperationException">
    /// The choice needs the other device's version, which no pull has brought yet, or the device is not connected.
    /// </exception>
    Task ResolveConflictAsync(Guid recordId, SyncConflictChoice choice, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the home directory and every synchronized directory below it, so the periodic sync can
    /// watch them for changes (ADR 0101 section 4); empty without a session.
    /// </summary>
    ValueTask<IReadOnlyList<VirtualPath>> ListSynchronizedDirectoriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IFileSystemSyncService"/> implementation.</summary>
public sealed class FileSystemSyncService : IFileSystemSyncService
{
    /// <summary>How long the server keeps a tombstone (ADR 0101 section 1, deferred decision 7).</summary>
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(90);

    /// <summary>
    /// Pseudo-domain of <see cref="ISyncRecordStateRepository"/> listing the home-relative paths the
    /// sync itself put in the trash (received deletions, replaced versions). Never sent to the server.
    /// </summary>
    internal const string SuppressedDomain = "filesystem.local-trash";

    /// <summary>Content hash carried by a tombstone envelope: SHA-256 of nothing.</summary>
    internal static readonly string TombstoneHash = ComputeHash([]);

    private const int MaxRecordsPerPush = 100;
    private const int PreviewCharacters = 2000;
    private const long MaxPreviewDownloadBytes = 1024 * 1024;

    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly IServerConnectionService _connection;
    private readonly ISyncClient _syncClient;
    private readonly IContentTransferClient _contentClient;
    private readonly ISyncCursorRepository _cursors;
    private readonly ISyncRecordStateRepository _recordState;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="fileSystem">Filesystem the home lives in.</param>
    /// <param name="session">Session giving the user and the home.</param>
    /// <param name="connection">Optional-server connection.</param>
    /// <param name="syncClient">Sync endpoints.</param>
    /// <param name="contentClient">Content-transfer endpoints.</param>
    /// <param name="cursors">Pull cursors.</param>
    /// <param name="recordState">Per-record tracking.</param>
    /// <param name="time">Clock; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="logger">Logger; its lines reach <c>/var/log/syslog</c> (ADR 0045).</param>
    public FileSystemSyncService(
        IFileSystemService fileSystem,
        ISessionService session,
        IServerConnectionService connection,
        ISyncClient syncClient,
        IContentTransferClient contentClient,
        ISyncCursorRepository cursors,
        ISyncRecordStateRepository recordState,
        TimeProvider? time = null,
        ILogger<FileSystemSyncService>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _syncClient = syncClient ?? throw new ArgumentNullException(nameof(syncClient));
        _contentClient = contentClient ?? throw new ArgumentNullException(nameof(contentClient));
        _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
        _recordState = recordState ?? throw new ArgumentNullException(nameof(recordState));
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public event EventHandler? ConflictsChanged;

    // ── Push ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<SyncPassResult> PushAsync(CancellationToken cancellationToken = default)
    {
        PassContext? pass = await OpenPassAsync(requireServer: true, cancellationToken).ConfigureAwait(false);
        if (pass is null)
        {
            return SyncPassResult.Empty;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PushCoreAsync(pass, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncPassResult> PushCoreAsync(PassContext pass, CancellationToken cancellationToken)
    {
        Dictionary<Guid, SyncRecordTrackingState> tracked = await LoadTrackedAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, Guid> recordByLocalId = MapLocalIds(tracked);
        List<SyncFileConflict> conflicts = await LoadConflictsAsync(pass, cancellationToken).ConfigureAwait(false);
        HashSet<Guid> frozen = [.. conflicts.Select(conflict => conflict.RecordId)];
        Dictionary<string, Guid> suppressed = await LoadSuppressedAsync(pass, cancellationToken).ConfigureAwait(false);

        WalkResult walk = await WalkAsync(pass, cancellationToken).ConfigureAwait(false);
        if (!walk.RootListed)
        {
            // Never mistake an unreadable home for an empty one: that would delete everything remotely.
            return SyncPassResult.Empty;
        }

        HashSet<Guid> seen = [];
        List<SyncRecordEnvelope> envelopes = [];
        Dictionary<Guid, SyncRecordTrackingState> pending = [];
        Dictionary<Guid, (VirtualPath Path, string ContentHash, long Length)> uploads = [];
        Dictionary<Guid, LocalDescription> described = [];

        foreach ((VirtualPath path, FileSystemEntryMetadata metadata) in walk.Entries)
        {
            string relativePath = GetRelativePath(pass.Home, path);
            if (IsSuppressed(relativePath, suppressed.Keys))
            {
                continue;
            }

            Guid localId = metadata.Id.Value;
            Guid recordId = recordByLocalId.TryGetValue(localId, out Guid mapped) ? mapped : localId;
            seen.Add(recordId);
            if (frozen.Contains(recordId))
            {
                continue; // ADR 0101 section 2: frozen until the user answers.
            }

            LocalDescription local = await DescribeAsync(pass, path, metadata, cancellationToken).ConfigureAwait(false);
            tracked.TryGetValue(recordId, out SyncRecordTrackingState? state);
            if (state is { IsTombstone: false } && string.Equals(state.ContentHash, local.ChangeHash, StringComparison.Ordinal))
            {
                if (state.UserLogin is null || state.RelativePath is null)
                {
                    // Backfill a state written before ADR 0101 so its deletion can be detected later.
                    await _recordState.SetAsync(
                        SyncDomain.FileSystem, recordId, Track(pass, state.Revision, local.ChangeHash, relativePath, localId, recordId),
                        cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            long revision = (state?.Revision ?? 0) + 1;
            envelopes.Add(new SyncRecordEnvelope(
                recordId, SyncDomain.FileSystem, SchemaVersion: 1, pass.Server!.AccountId, pass.Server!.DeviceId,
                revision, _time.GetUtcNow(), ServerReceivedUtc: null, local.Hash, IsTombstone: false, local.PayloadJson));
            pending[recordId] = Track(pass, revision, local.ChangeHash, relativePath, localId, recordId);
            described[recordId] = local;
            if (local.Payload.ContentHash is { } contentHash)
            {
                uploads[recordId] = (path, contentHash, local.Payload.Length ?? 0);
            }
        }

        // ADR 0101 section 1: a synchronized entry of this user that left the home is a deletion.
        foreach ((Guid recordId, SyncRecordTrackingState state) in tracked)
        {
            if (state.IsTombstone
                || seen.Contains(recordId)
                || frozen.Contains(recordId)
                || state.RelativePath is not { } relativePath
                || !string.Equals(state.UserLogin, pass.Login, StringComparison.Ordinal)
                || IsDeviceLocal(relativePath)
                || walk.UnlistedDirectories.Any(directory => IsSameOrBelow(relativePath, directory)))
            {
                continue;
            }

            long revision = state.Revision + 1;
            envelopes.Add(new SyncRecordEnvelope(
                recordId, SyncDomain.FileSystem, SchemaVersion: 1, pass.Server!.AccountId, pass.Server!.DeviceId,
                revision, _time.GetUtcNow(), ServerReceivedUtc: null, TombstoneHash, IsTombstone: true, PayloadJson: null));
            pending[recordId] = new SyncRecordTrackingState(revision, TombstoneHash, relativePath, null, IsTombstone: true, pass.Login);
        }

        await ForgetVanishedSuppressionsAsync(walk, pass, suppressed, cancellationToken).ConfigureAwait(false);
        if (envelopes.Count == 0)
        {
            return SyncPassResult.Empty;
        }

        // Upload changed file content before pushing metadata, so a pushed record never points at
        // content the server doesn't have yet.
        foreach ((VirtualPath path, string contentHash, long _) in uploads.Values.DistinctBy(upload => upload.ContentHash))
        {
            byte[] content = await pass.Files.TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) ?? [];
            await UploadContentAsync(pass, contentHash, content, cancellationToken).ConfigureAwait(false);
        }

        int sent = 0;
        int found = 0;
        foreach (SyncRecordEnvelope[] batch in envelopes.Chunk(MaxRecordsPerPush))
        {
            PushResponse response = await _syncClient.PushAsync(
                pass.ServerUrl, pass.AccessToken, new PushRequest(SyncDomain.FileSystem, batch, Guid.NewGuid()), cancellationToken)
                .ConfigureAwait(false);
            Dictionary<Guid, SyncConflict> refused = response.Conflicts
                .GroupBy(conflict => conflict.RecordId)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (SyncRecordEnvelope envelope in batch)
            {
                if (refused.TryGetValue(envelope.RecordId, out SyncConflict? conflict))
                {
                    if (conflict.ConflictCode == "CONCURRENT_EDIT")
                    {
                        // ADR 0101 section 2: never pick a side for the user. The pull that follows
                        // fills in the other device's version.
                        SyncRecordTrackingState state = pending[envelope.RecordId];
                        conflicts.Add(new SyncFileConflict(
                            envelope.RecordId,
                            state.RelativePath!,
                            _time.GetUtcNow(),
                            envelope.IsTombstone
                                ? new SyncFileVersion(true, null, null, null, pass.Server!.DeviceId)
                                : described[envelope.RecordId].ToVersion(pass.Server!.DeviceId),
                            Remote: null,
                            RemoteRevision: Math.Max(conflict.ServerRevision, 0),
                            RemotePayloadJson: null));
                        found++;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "sync: filesystem record {RecordId} refused by the server ({Code})", envelope.RecordId, conflict.ConflictCode);
                    }

                    continue;
                }

                await _recordState.SetAsync(SyncDomain.FileSystem, envelope.RecordId, pending[envelope.RecordId], cancellationToken)
                    .ConfigureAwait(false);
                sent++;
            }
        }

        if (found > 0)
        {
            await SaveConflictsAsync(pass, conflicts, cancellationToken).ConfigureAwait(false);
        }

        return new SyncPassResult(sent, 0, found);
    }

    // ── Pull ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<SyncPassResult> PullAsync(CancellationToken cancellationToken = default)
    {
        PassContext? pass = await OpenPassAsync(requireServer: true, cancellationToken).ConfigureAwait(false);
        if (pass is null)
        {
            return SyncPassResult.Empty;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PullCoreAsync(pass, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncPassResult> PullCoreAsync(PassContext pass, CancellationToken cancellationToken)
    {
        Dictionary<Guid, SyncRecordTrackingState> tracked = await LoadTrackedAsync(cancellationToken).ConfigureAwait(false);
        List<SyncFileConflict> conflicts = await LoadConflictsAsync(pass, cancellationToken).ConfigureAwait(false);
        bool conflictsChanged = false;
        int received = 0;
        int found = 0;

        DateTimeOffset now = _time.GetUtcNow();
        string? cursor = await _cursors.GetCursorAsync(SyncDomain.FileSystem, cancellationToken).ConfigureAwait(false);
        DateTimeOffset? lastPulled = await _cursors.GetLastPulledAtAsync(SyncDomain.FileSystem, cancellationToken).ConfigureAwait(false);

        // ADR 0101 section 1: past the tombstone retention, deletions may have been purged from the
        // server, so a cursor would miss them. Read everything again and compare instead.
        bool fullResync = cursor is not null && lastPulled is { } last && now - last > TombstoneRetention;
        if (fullResync)
        {
            _logger.LogInformation(
                "sync: filesystem: offline since {LastPull:O}, longer than the {Days}-day tombstone retention; full resynchronization",
                lastPulled, TombstoneRetention.TotalDays);
            cursor = null;
        }

        HashSet<Guid> remoteRecords = [];
        while (true)
        {
            PullResponse response = await _syncClient.PullAsync(
                pass.ServerUrl, pass.AccessToken, new PullRequest(SyncDomain.FileSystem, cursor, MaxRecords: 100), cancellationToken)
                .ConfigureAwait(false);

            // Only the newest revision of each record in the page matters.
            List<SyncRecordEnvelope> latest = [.. response.Records
                .GroupBy(envelope => envelope.RecordId)
                .Select(group => group.MaxBy(envelope => envelope.Revision)!)];

            List<(SyncRecordEnvelope Envelope, FileSystemSyncPayload Payload)> changes = [];
            List<SyncRecordEnvelope> deletions = [];
            foreach (SyncRecordEnvelope envelope in latest)
            {
                remoteRecords.Add(envelope.RecordId);
                tracked.TryGetValue(envelope.RecordId, out SyncRecordTrackingState? state);
                if (state is not null && envelope.Revision <= state.Revision)
                {
                    continue; // Already applied, or this device's own push coming back.
                }

                if (conflicts.FindIndex(conflict => conflict.RecordId == envelope.RecordId) is var index and >= 0)
                {
                    // A frozen file (ADR 0101 section 2) is never replaced: remember the other
                    // device's version for the dialog instead.
                    conflicts[index] = conflicts[index] with
                    {
                        Remote = DescribeRemote(envelope),
                        RemoteRevision = envelope.Revision,
                        RemotePayloadJson = envelope.IsTombstone ? null : envelope.PayloadJson
                    };
                    conflictsChanged = true;
                    if (await TryAutoResolveAsync(pass, conflicts[index], cancellationToken).ConfigureAwait(false))
                    {
                        conflicts.RemoveAt(index);
                    }

                    continue;
                }

                if (envelope.IsTombstone || envelope.PayloadJson is null)
                {
                    deletions.Add(envelope);
                }
                else
                {
                    changes.Add((envelope, JsonSerializer.Deserialize(
                        envelope.PayloadJson, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!));
                }
            }

            // Applied shallowest-path-first within the page so a directory record is always created
            // before any file/symlink record nested under it (ADR 0030's ordering simplification).
            foreach ((SyncRecordEnvelope envelope, FileSystemSyncPayload payload) in changes.OrderBy(pair => Depth(pair.Payload.RelativePath)))
            {
                tracked.TryGetValue(envelope.RecordId, out SyncRecordTrackingState? state);
                if (IsDeviceLocal(payload.RelativePath))
                {
                    continue; // Another device's local state (ADR 0044 section 10) never overrides this one's.
                }

                if (state is not { IsTombstone: false } || state.RelativePath != payload.RelativePath)
                {
                    PathClaim claim = await ClaimPathAsync(pass, tracked, conflicts, envelope, payload, now, cancellationToken)
                        .ConfigureAwait(false);
                    if (claim == PathClaim.Conflict)
                    {
                        conflictsChanged = true;
                        found++;
                    }

                    if (claim != PathClaim.Free)
                    {
                        continue;
                    }
                }

                if (await HasLocalChangeAsync(pass, state, cancellationToken).ConfigureAwait(false) is { } localChange)
                {
                    conflicts.Add(new SyncFileConflict(
                        envelope.RecordId, state!.RelativePath!, now, localChange, DescribeRemote(envelope), envelope.Revision, envelope.PayloadJson));
                    conflictsChanged = true;
                    found++;
                    continue;
                }

                await ApplyChangeAsync(pass, envelope.RecordId, envelope.Revision, payload, state, cancellationToken).ConfigureAwait(false);
                await RefreshTrackedAsync(tracked, envelope.RecordId, cancellationToken).ConfigureAwait(false);
                received++;
            }

            // Deletions last, parents first: trashing a directory takes its children with it.
            foreach (SyncRecordEnvelope envelope in deletions.OrderBy(envelope =>
                         tracked.TryGetValue(envelope.RecordId, out SyncRecordTrackingState? state) && state.RelativePath is { } path ? Depth(path) : int.MaxValue))
            {
                tracked.TryGetValue(envelope.RecordId, out SyncRecordTrackingState? state);
                if (await HasLocalChangeAsync(pass, state, cancellationToken).ConfigureAwait(false) is { } localChange)
                {
                    // Modified here, deleted on the other device: the user decides.
                    conflicts.Add(new SyncFileConflict(
                        envelope.RecordId, state!.RelativePath!, now, localChange, DescribeRemote(envelope), envelope.Revision, null));
                    conflictsChanged = true;
                    found++;
                    continue;
                }

                if (await ApplyDeletionAsync(pass, tracked, envelope.RecordId, envelope.Revision, envelope.OriginDeviceId, state, cancellationToken)
                        .ConfigureAwait(false))
                {
                    received++;
                }

                await RefreshTrackedAsync(tracked, envelope.RecordId, cancellationToken).ConfigureAwait(false);
            }

            if (response.NextCursor is not null)
            {
                cursor = response.NextCursor;
                await _cursors.SetCursorAsync(SyncDomain.FileSystem, cursor, cancellationToken).ConfigureAwait(false);
            }

            if (!response.HasMore)
            {
                break;
            }
        }

        if (fullResync)
        {
            // A record this user synchronized that the server no longer knows at all was deleted
            // elsewhere and its tombstone purged: handle it like a received deletion.
            foreach ((Guid recordId, SyncRecordTrackingState state) in tracked.ToList())
            {
                if (state.IsTombstone
                    || remoteRecords.Contains(recordId)
                    || !string.Equals(state.UserLogin, pass.Login, StringComparison.Ordinal)
                    || conflicts.Any(conflict => conflict.RecordId == recordId))
                {
                    continue;
                }

                if (await ApplyDeletionAsync(pass, tracked, recordId, state.Revision, Guid.Empty, state, cancellationToken).ConfigureAwait(false))
                {
                    received++;
                }
            }
        }

        await _cursors.SetLastPulledAtAsync(SyncDomain.FileSystem, now, cancellationToken).ConfigureAwait(false);
        if (conflictsChanged)
        {
            await SaveConflictsAsync(pass, conflicts, cancellationToken).ConfigureAwait(false);
        }

        return new SyncPassResult(0, received, found);
    }

    /// <summary>
    /// Writes one remote change: follows a rename first (the local entry moves with its record),
    /// then creates or rewrites the entry, and tracks the local result.
    /// </summary>
    private async Task ApplyChangeAsync(
        PassContext pass,
        Guid recordId,
        long revision,
        FileSystemSyncPayload payload,
        SyncRecordTrackingState? state,
        CancellationToken cancellationToken)
    {
        VirtualPath path = ToAbsolute(pass, payload.RelativePath);
        if (state is { IsTombstone: false, RelativePath: { } previous } && previous != payload.RelativePath)
        {
            VirtualPath previousPath = ToAbsolute(pass, previous);
            if (await pass.Files.StatAsync(previousPath, cancellationToken).ConfigureAwait(false) is not null
                && await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false) is null)
            {
                await pass.Files.MoveAsync(previousPath, path, cancellationToken).ConfigureAwait(false);
            }
        }

        FileSystemPermissions permissions = FileSystemPermissions.FromMode(payload.PermissionMode);
        if (string.Equals(payload.Kind, nameof(FileSystemEntryKind.Directory), StringComparison.Ordinal))
        {
            await pass.Files.EnsureDirectoryAsync(path, permissions, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(payload.Kind, nameof(FileSystemEntryKind.SymbolicLink), StringComparison.Ordinal)
                 && payload.SymlinkTarget is not null)
        {
            await _fileSystem.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.SymbolicLink, permissions, symbolicLinkTarget: payload.SymlinkTarget),
                pass.Authorization, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(payload.Kind, nameof(FileSystemEntryKind.File), StringComparison.Ordinal)
                 && payload.ContentHash is not null)
        {
            byte[] content = await DownloadContentAsync(pass, payload.ContentHash, cancellationToken).ConfigureAwait(false);
            await pass.Files.WriteAllBytesAsync(path, content, permissions, cancellationToken).ConfigureAwait(false);
        }

        // Track the local result's own description, so the next push sees "unchanged" instead of
        // sending the file straight back with this device's timestamps.
        FileSystemEntrySnapshot? applied = await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (applied is null)
        {
            return;
        }

        LocalDescription local = await DescribeAsync(pass, path, applied.Metadata, cancellationToken).ConfigureAwait(false);
        await _recordState.SetAsync(
            SyncDomain.FileSystem, recordId, Track(pass, revision, local.ChangeHash, payload.RelativePath, applied.Metadata.Id.Value, recordId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Outcome of <see cref="ClaimPathAsync"/>.</summary>
    private enum PathClaim
    {
        /// <summary>No other record holds the path: apply the change normally.</summary>
        Free,

        /// <summary>The path is handled (adopted, or kept by the record that wins it): nothing to apply.</summary>
        Handled,

        /// <summary>Both devices created different files at the same path: the user decides.</summary>
        Conflict
    }

    /// <summary>
    /// Settles two records claiming the same path, which happens whenever two devices create the
    /// same entry independently (every device seeds the same standard directories at first login).
    /// </summary>
    /// <remarks>
    /// The record with the smallest id keeps the path, the same rule on every device so they all
    /// converge: when the incoming record wins, the local entry is re-tracked under it and the record
    /// that lost is left untracked locally, so the next push sends its tombstone (which every device
    /// that adopted the winner ignores); when the local record wins, the incoming one is ignored and
    /// the other device adopts the local one on its next pull. Two different files at the same path
    /// become a conflict (ADR 0101 section 2) instead of one silently replacing the other.
    /// </remarks>
    private async Task<PathClaim> ClaimPathAsync(
        PassContext pass,
        Dictionary<Guid, SyncRecordTrackingState> tracked,
        List<SyncFileConflict> conflicts,
        SyncRecordEnvelope envelope,
        FileSystemSyncPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        VirtualPath path = ToAbsolute(pass, payload.RelativePath);
        if (await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return PathClaim.Free;
        }

        Guid localId = entry.Metadata.Id.Value;
        Guid holder = OwnerOf(tracked, localId);
        if (holder == envelope.RecordId)
        {
            return PathClaim.Free;
        }

        if (holder.CompareTo(envelope.RecordId) < 0 || conflicts.Any(conflict => conflict.RecordId == holder))
        {
            return PathClaim.Handled; // The local record keeps the path (or is frozen until the user answers).
        }

        if (tracked.TryGetValue(holder, out SyncRecordTrackingState? lost) && !lost.IsTombstone)
        {
            await _recordState.SetAsync(SyncDomain.FileSystem, holder, lost with { LocalEntryId = null }, cancellationToken)
                .ConfigureAwait(false);
            tracked[holder] = lost with { LocalEntryId = null };
        }

        LocalDescription local = await DescribeAsync(pass, path, entry.Metadata, cancellationToken).ConfigureAwait(false);
        SyncRecordTrackingState adopted = Track(pass, envelope.Revision, local.ChangeHash, payload.RelativePath, localId, envelope.RecordId);
        await _recordState.SetAsync(SyncDomain.FileSystem, envelope.RecordId, adopted, cancellationToken).ConfigureAwait(false);
        tracked[envelope.RecordId] = adopted;

        bool sameEntry = string.Equals(payload.Kind, entry.Metadata.Kind.ToString(), StringComparison.Ordinal)
            && (entry.Metadata.Kind == FileSystemEntryKind.Directory
                || string.Equals(payload.ContentHash, local.Payload.ContentHash, StringComparison.Ordinal)
                && string.Equals(payload.SymlinkTarget, local.Payload.SymlinkTarget, StringComparison.Ordinal));
        if (sameEntry)
        {
            return PathClaim.Handled;
        }

        conflicts.Add(new SyncFileConflict(
            envelope.RecordId, payload.RelativePath, now, local.ToVersion(pass.Server?.DeviceId), DescribeRemote(envelope), envelope.Revision,
            envelope.PayloadJson));
        return PathClaim.Conflict;
    }

    /// <summary>Gets the record a local entry is synchronized as: the record that adopted it, else its own id.</summary>
    private static Guid OwnerOf(Dictionary<Guid, SyncRecordTrackingState> tracked, Guid localId)
    {
        foreach ((Guid recordId, SyncRecordTrackingState state) in tracked)
        {
            if (!state.IsTombstone && state.LocalEntryId == localId)
            {
                return recordId;
            }
        }

        return localId;
    }

    /// <summary>
    /// Applies a received deletion (ADR 0101 section 1): the local entry goes to the user's trash,
    /// marked "deleted on another device", or is deleted for good when it already was in the trash.
    /// </summary>
    /// <returns>Whether a local entry was affected.</returns>
    private async Task<bool> ApplyDeletionAsync(
        PassContext pass,
        Dictionary<Guid, SyncRecordTrackingState> tracked,
        Guid recordId,
        long revision,
        Guid originDeviceId,
        SyncRecordTrackingState? state,
        CancellationToken cancellationToken)
    {
        bool affected = false;
        if (state is { IsTombstone: false, RelativePath: { } relativePath })
        {
            VirtualPath path = ToAbsolute(pass, relativePath);
            // The entry at that path may now belong to another record (see ClaimPathAsync): the
            // deletion of the record that lost the path never touches it.
            if (await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false) is { } entry
                && OwnerOf(tracked, entry.Metadata.Id.Value) == recordId)
            {
                await TrashLocallyAsync(pass, path, originDeviceId, cancellationToken).ConfigureAwait(false);
                affected = true;
            }
        }

        await _recordState.SetAsync(
            SyncDomain.FileSystem,
            recordId,
            new SyncRecordTrackingState(revision, TombstoneHash, state?.RelativePath, null, IsTombstone: true, pass.Login),
            cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <summary>Moves a local entry to the trash and keeps that trashed copy out of the sync.</summary>
    private async Task TrashLocallyAsync(PassContext pass, VirtualPath path, Guid originDeviceId, CancellationToken cancellationToken)
    {
        DateTimeOffset deletedAt = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);
        (string ElementPath, string InfoPath)? trashed = await pass.Files.MoveToTrashAsync(
            pass.Home, path, originDeviceId, deletedAt, cancellationToken).ConfigureAwait(false);
        if (trashed is { } item)
        {
            await SuppressAsync(pass, item.ElementPath, cancellationToken).ConfigureAwait(false);
            await SuppressAsync(pass, item.InfoPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("sync: '{Path}' was deleted on another device; moved to the trash", path.Value);
        }
        else
        {
            _logger.LogInformation("sync: '{Path}' was emptied from the trash on another device; deleted", path.Value);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<VirtualPath>> ListSynchronizedDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        PassContext? pass = await OpenPassAsync(requireServer: false, cancellationToken).ConfigureAwait(false);
        if (pass is null)
        {
            return [];
        }

        WalkResult walk = await WalkAsync(pass, cancellationToken).ConfigureAwait(false);
        return walk.RootListed
            ? [pass.Home, .. walk.Entries.Where(entry => entry.Metadata.Kind == FileSystemEntryKind.Directory).Select(entry => entry.Path)]
            : [];
    }

    // ── Conflicts ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SyncFileConflict>> GetConflictsAsync(CancellationToken cancellationToken = default)
    {
        PassContext? pass = await OpenPassAsync(requireServer: false, cancellationToken).ConfigureAwait(false);
        return pass is null ? [] : await LoadConflictsAsync(pass, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SyncConflictPreview> GetConflictPreviewAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        PassContext pass = await OpenPassAsync(requireServer: false, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("No session: no pending conflict.");
        SyncFileConflict conflict = (await LoadConflictsAsync(pass, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.RecordId == recordId)
            ?? throw new KeyNotFoundException($"No pending conflict for record {recordId}.");

        string? localText = null;
        if (!conflict.Local.IsDeleted)
        {
            byte[]? bytes = await pass.Files.TryReadAllBytesAsync(ToAbsolute(pass, conflict.RelativePath), cancellationToken).ConfigureAwait(false);
            localText = ToPreviewText(bytes);
        }

        string? remoteText = null;
        if (conflict.Remote is { IsDeleted: false, ContentHash: { } remoteHash, Length: { } length }
            && length <= MaxPreviewDownloadBytes
            && pass.Server is not null)
        {
            try
            {
                remoteText = ToPreviewText(await DownloadContentAsync(pass, remoteHash, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or KeyNotFoundException)
            {
                remoteText = null; // Offline: the dialog shows the facts without a preview.
            }
        }

        return new SyncConflictPreview(localText, remoteText);
    }

    /// <inheritdoc />
    public async Task ResolveConflictAsync(Guid recordId, SyncConflictChoice choice, CancellationToken cancellationToken = default)
    {
        PassContext pass = await OpenPassAsync(requireServer: true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Resolving a conflict needs a session and a connection to the server.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<SyncFileConflict> conflicts = await LoadConflictsAsync(pass, cancellationToken).ConfigureAwait(false);
            int index = conflicts.FindIndex(candidate => candidate.RecordId == recordId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"No pending conflict for record {recordId}.");
            }

            SyncFileConflict conflict = conflicts[index];
            if (choice != SyncConflictChoice.KeepMine && conflict.Remote is null)
            {
                throw new InvalidOperationException(
                    "The other device's version has not been received yet; synchronize again before choosing it.");
            }

            await ResolveCoreAsync(pass, conflict, choice, cancellationToken).ConfigureAwait(false);
            conflicts.RemoveAt(index);
            await SaveConflictsAsync(pass, conflicts, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("sync: conflict on '{Path}' resolved: {Choice}", conflict.RelativePath, choice);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResolveCoreAsync(PassContext pass, SyncFileConflict conflict, SyncConflictChoice choice, CancellationToken cancellationToken)
    {
        VirtualPath path = ToAbsolute(pass, conflict.RelativePath);
        FileSystemEntrySnapshot? local = await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false);
        Guid localId = local?.Metadata.Id.Value ?? conflict.RecordId;

        if (choice == SyncConflictChoice.KeepMine)
        {
            if (local is null)
            {
                // Deleted here: acknowledge the server's revision, then the next push sends the
                // deletion on top of it.
                ResolveSyncConflictResponse acknowledged = await ResolveOnServerAsync(
                    pass, conflict.RecordId, ConflictResolution.ServerWins, null, null, cancellationToken).ConfigureAwait(false);
                await _recordState.SetAsync(
                    SyncDomain.FileSystem,
                    conflict.RecordId,
                    new SyncRecordTrackingState(
                        Math.Max(acknowledged.FinalRevision, conflict.RemoteRevision), TombstoneHash, conflict.RelativePath, null, false, pass.Login),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            LocalDescription description = await DescribeAsync(pass, path, local.Metadata, cancellationToken).ConfigureAwait(false);
            if (description.Payload.ContentHash is { } contentHash)
            {
                byte[] content = await pass.Files.TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) ?? [];
                await UploadContentAsync(pass, contentHash, content, cancellationToken).ConfigureAwait(false);
            }

            ResolveSyncConflictResponse resolved = await ResolveOnServerAsync(
                pass, conflict.RecordId, ConflictResolution.ClientWins, description.PayloadJson, description.Hash, cancellationToken)
                .ConfigureAwait(false);
            await _recordState.SetAsync(
                SyncDomain.FileSystem,
                conflict.RecordId,
                Track(pass, resolved.FinalRevision, description.ChangeHash, conflict.RelativePath, localId, conflict.RecordId),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        SyncFileVersion remote = conflict.Remote!;
        await ResolveOnServerAsync(pass, conflict.RecordId, ConflictResolution.ServerWins, null, null, cancellationToken).ConfigureAwait(false);

        if (choice == SyncConflictChoice.KeepBoth && local is { Metadata: FileMetadata })
        {
            // The local version stays next to the original as a brand-new file (new entry, new
            // record), then the server's version takes the original path.
            byte[] content = await pass.Files.TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) ?? [];
            VirtualPath copy = await FreeConflictCopyPathAsync(pass, path, cancellationToken).ConfigureAwait(false);
            await pass.Files.WriteAllBytesAsync(copy, content, local.Metadata.Permissions, cancellationToken).ConfigureAwait(false);
            if (remote.IsDeleted)
            {
                await pass.Files.DeleteRecursiveAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (local is not null)
        {
            // Keep the other device's version: the local one goes to the trash, recoverable.
            await TrashLocallyAsync(pass, path, remote.DeviceId ?? Guid.Empty, cancellationToken).ConfigureAwait(false);
        }

        SyncRecordTrackingState? state = await _recordState.GetAsync(SyncDomain.FileSystem, conflict.RecordId, cancellationToken).ConfigureAwait(false);
        if (remote.IsDeleted || conflict.RemotePayloadJson is null)
        {
            await _recordState.SetAsync(
                SyncDomain.FileSystem,
                conflict.RecordId,
                new SyncRecordTrackingState(conflict.RemoteRevision, TombstoneHash, conflict.RelativePath, null, IsTombstone: true, pass.Login),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        FileSystemSyncPayload payload = JsonSerializer.Deserialize(
            conflict.RemotePayloadJson, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!;
        await ApplyChangeAsync(pass, conflict.RecordId, conflict.RemoteRevision, payload, state is { IsTombstone: false } ? state : null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves without asking when there is nothing to choose: both sides deleted the file, or both
    /// ended with the same content.
    /// </summary>
    private async Task<bool> TryAutoResolveAsync(PassContext pass, SyncFileConflict conflict, CancellationToken cancellationToken)
    {
        if (conflict.Remote is not { } remote)
        {
            return false;
        }

        bool bothDeleted = remote.IsDeleted && conflict.Local.IsDeleted;
        bool sameContent = !remote.IsDeleted && !conflict.Local.IsDeleted
            && remote.ContentHash is not null
            && string.Equals(remote.ContentHash, conflict.Local.ContentHash, StringComparison.Ordinal);
        if (!bothDeleted && !sameContent)
        {
            return false;
        }

        await ResolveOnServerAsync(pass, conflict.RecordId, ConflictResolution.ServerWins, null, null, cancellationToken).ConfigureAwait(false);
        if (bothDeleted)
        {
            await _recordState.SetAsync(
                SyncDomain.FileSystem,
                conflict.RecordId,
                new SyncRecordTrackingState(conflict.RemoteRevision, TombstoneHash, conflict.RelativePath, null, IsTombstone: true, pass.Login),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            VirtualPath path = ToAbsolute(pass, conflict.RelativePath);
            if (await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                LocalDescription local = await DescribeAsync(pass, path, entry.Metadata, cancellationToken).ConfigureAwait(false);
                await _recordState.SetAsync(
                    SyncDomain.FileSystem,
                    conflict.RecordId,
                    Track(pass, conflict.RemoteRevision, local.ChangeHash, conflict.RelativePath, entry.Metadata.Id.Value, conflict.RecordId),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("sync: conflict on '{Path}' resolved automatically (same result on both devices)", conflict.RelativePath);
        return true;
    }

    private async Task<ResolveSyncConflictResponse> ResolveOnServerAsync(
        PassContext pass, Guid recordId, ConflictResolution resolution, string? payloadJson, string? payloadHash, CancellationToken cancellationToken)
    {
        ResolveSyncConflictResponse response = await _syncClient.ResolveConflictAsync(
            pass.ServerUrl,
            pass.AccessToken,
            new ResolveSyncConflictRequest(recordId, SyncDomain.FileSystem, resolution, payloadJson, payloadHash),
            cancellationToken).ConfigureAwait(false);
        return response.Resolved
            ? response
            : throw new ServerConnectionException($"The server refused the conflict resolution: {response.Reason}", response.Reason);
    }

    /// <summary>
    /// Builds <c>&lt;name&gt;.sync-conflict-&lt;yyyyMMdd-HHmmss&gt;-&lt;device&gt;.&lt;ext&gt;</c> next to
    /// <paramref name="path"/> (ADR 0101 section 2), with a numeric suffix if that name is taken.
    /// </summary>
    private async Task<VirtualPath> FreeConflictCopyPathAsync(PassContext pass, VirtualPath path, CancellationToken cancellationToken)
    {
        string directory = path.Value[..path.Value.LastIndexOf('/')];
        string fileName = path.Value[(directory.Length + 1)..];
        string stamp = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string device = pass.Server!.DeviceId.ToString("N")[..8];
        for (int attempt = 1; ; attempt++)
        {
            string candidate = BuildConflictCopyName(fileName, stamp, attempt == 1 ? device : $"{device}-{attempt}");
            VirtualPath copy = VirtualPath.Parse($"{directory}/{candidate}");
            if (await pass.Files.StatAsync(copy, cancellationToken).ConfigureAwait(false) is null)
            {
                return copy;
            }
        }
    }

    /// <summary>Gets the conflict-copy name of a file: the extension stays last so the file still opens with the same app.</summary>
    /// <param name="fileName">Original file name.</param>
    /// <param name="stamp">Local time, <c>yyyyMMdd-HHmmss</c>.</param>
    /// <param name="device">Short device name.</param>
    public static string BuildConflictCopyName(string fileName, string stamp, string device)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0
            ? $"{fileName[..dot]}.sync-conflict-{stamp}-{device}{fileName[dot..]}"
            : $"{fileName}.sync-conflict-{stamp}-{device}";
    }

    /// <summary>
    /// Gets whether the entry a tracked state points at changed locally since it was last synchronized;
    /// returns its current description, or <see langword="null"/> when it did not change (or cannot tell).
    /// </summary>
    private async Task<SyncFileVersion?> HasLocalChangeAsync(PassContext pass, SyncRecordTrackingState? state, CancellationToken cancellationToken)
    {
        if (state is not { IsTombstone: false, RelativePath: { } relativePath, UserLogin: not null })
        {
            return null; // Never synchronized here, or tracked before ADR 0101 (cannot tell).
        }

        VirtualPath path = ToAbsolute(pass, relativePath);
        FileSystemEntrySnapshot? entry = await pass.Files.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null; // Gone locally too: the next push sends that deletion.
        }

        LocalDescription local = await DescribeAsync(pass, path, entry.Metadata, cancellationToken).ConfigureAwait(false);
        return string.Equals(local.ChangeHash, state.ContentHash, StringComparison.Ordinal) || entry.Metadata.Kind == FileSystemEntryKind.Directory
            ? null
            : local.ToVersion(pass.Server?.DeviceId);
    }

    private async Task<List<SyncFileConflict>> LoadConflictsAsync(PassContext pass, CancellationToken cancellationToken)
    {
        byte[]? content = await pass.Files.TryReadAllBytesAsync(ToAbsolute(pass, SyncConflictFileFormat.RelativePath), cancellationToken)
            .ConfigureAwait(false);
        return SyncConflictFileFormat.Parse(content);
    }

    private async Task SaveConflictsAsync(PassContext pass, List<SyncFileConflict> conflicts, CancellationToken cancellationToken)
    {
        VirtualPath file = ToAbsolute(pass, SyncConflictFileFormat.RelativePath);
        await pass.Files.EnsureDirectoryAsync(
            VirtualPath.Parse(file.Value[..file.Value.LastIndexOf('/')]), FileSystemPermissions.FromMode(0b111_000_000), cancellationToken)
            .ConfigureAwait(false);
        await pass.Files.WriteAllBytesAsync(
            file, SyncConflictFileFormat.Format(conflicts), FileSystemPermissions.FromMode(0b110_000_000), cancellationToken).ConfigureAwait(false);
        ConflictsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Shared plumbing ──────────────────────────────────────────────────────

    /// <summary>Everything one pass needs: the user, the home, the server link.</summary>
    private sealed record PassContext(
        string Login,
        VirtualPath Home,
        FileSystemAuthorizationContext Authorization,
        SyncFileOperations Files,
        ServerConnectionState? Server,
        string? Token)
    {
        public Uri ServerUrl => new(Server!.ServerBaseUrl);

        public string AccessToken => Token!;
    }

    private async Task<PassContext?> OpenPassAsync(bool requireServer, CancellationToken cancellationToken)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        if (principal is null)
        {
            return null;
        }

        ServerConnectionState? state = await _connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        string? accessToken = null;
        if (requireServer)
        {
            accessToken = state is null ? null : await _connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (state is null || accessToken is null)
            {
                return null;
            }
        }

        FileSystemAuthorizationContext authorization = CreateAuthorizationContext(principal);
        return new PassContext(
            principal.LoginName.Value,
            VirtualPath.Parse(principal.HomePath),
            authorization,
            new SyncFileOperations(_fileSystem, authorization),
            state,
            accessToken);
    }

    private async Task<Dictionary<Guid, SyncRecordTrackingState>> LoadTrackedAsync(CancellationToken cancellationToken) =>
        (await _recordState.ListAsync(SyncDomain.FileSystem, cancellationToken).ConfigureAwait(false))
        .ToDictionary(record => record.RecordId, record => record.State);

    private async Task RefreshTrackedAsync(Dictionary<Guid, SyncRecordTrackingState> tracked, Guid recordId, CancellationToken cancellationToken)
    {
        if (await _recordState.GetAsync(SyncDomain.FileSystem, recordId, cancellationToken).ConfigureAwait(false) is { } state)
        {
            tracked[recordId] = state;
        }
    }

    /// <summary>Maps the local entry ids that carry another record id (entries created by a pull).</summary>
    private static Dictionary<Guid, Guid> MapLocalIds(Dictionary<Guid, SyncRecordTrackingState> tracked)
    {
        Dictionary<Guid, Guid> map = [];
        foreach ((Guid recordId, SyncRecordTrackingState state) in tracked)
        {
            if (!state.IsTombstone && state.LocalEntryId is { } localId)
            {
                map[localId] = recordId;
            }
        }

        return map;
    }

    private SyncRecordTrackingState Track(PassContext pass, long revision, string hash, string relativePath, Guid localId, Guid recordId) =>
        new(revision, hash, relativePath, localId == recordId ? null : localId, IsTombstone: false, pass.Login);

    private async Task<Dictionary<string, Guid>> LoadSuppressedAsync(PassContext pass, CancellationToken cancellationToken)
    {
        Dictionary<string, Guid> paths = new(StringComparer.Ordinal);
        foreach (SyncTrackedRecord record in await _recordState.ListAsync(SuppressedDomain, cancellationToken).ConfigureAwait(false))
        {
            if (record.State.RelativePath is { } path && string.Equals(record.State.UserLogin, pass.Login, StringComparison.Ordinal))
            {
                paths[path] = record.RecordId;
            }
        }

        return paths;
    }

    private ValueTask SuppressAsync(PassContext pass, string relativePath, CancellationToken cancellationToken) =>
        _recordState.SetAsync(
            SuppressedDomain,
            new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{pass.Login}|{relativePath}")).AsSpan(0, 16)),
            new SyncRecordTrackingState(0, TombstoneHash, relativePath, null, false, pass.Login),
            cancellationToken);

    /// <summary>A suppressed trash item the user restored or emptied no longer needs to be kept out.</summary>
    private async Task ForgetVanishedSuppressionsAsync(
        WalkResult walk, PassContext pass, Dictionary<string, Guid> suppressed, CancellationToken cancellationToken)
    {
        if (suppressed.Count == 0)
        {
            return;
        }

        HashSet<string> present = [.. walk.Entries.Select(entry => GetRelativePath(pass.Home, entry.Path))];
        foreach ((string path, Guid id) in suppressed)
        {
            if (!present.Contains(path) && !walk.UnlistedDirectories.Any(directory => IsSameOrBelow(path, directory)))
            {
                await _recordState.RemoveAsync(SuppressedDomain, id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSuppressed(string relativePath, IEnumerable<string> suppressed) =>
        suppressed.Any(path => IsSameOrBelow(relativePath, path));

    private static bool IsSameOrBelow(string relativePath, string directory) =>
        relativePath == directory || relativePath.StartsWith(directory + "/", StringComparison.Ordinal);

    /// <summary>The payload, its JSON and hash for one local entry.</summary>
    /// <param name="Payload">What is sent to the server.</param>
    /// <param name="PayloadJson">Serialized <paramref name="Payload"/>.</param>
    /// <param name="Hash">Hash of <paramref name="PayloadJson"/>, the envelope's content hash.</param>
    /// <param name="ChangeHash">
    /// What change detection compares: <paramref name="Hash"/>, except for a directory, whose
    /// modification times change every time an entry is added or removed inside it. Only its
    /// name, owner and mode count, so a new file never re-sends every parent directory.
    /// </param>
    private sealed record LocalDescription(FileSystemSyncPayload Payload, string PayloadJson, string Hash, string ChangeHash)
    {
        public SyncFileVersion ToVersion(Guid? deviceId) =>
            new(false, Payload.ContentModifiedAtUtc, Payload.Length, Payload.ContentHash, deviceId);
    }

    private async Task<LocalDescription> DescribeAsync(
        PassContext pass, VirtualPath path, FileSystemEntryMetadata metadata, CancellationToken cancellationToken)
    {
        string? contentHash = null;
        long? length = null;
        string? symlinkTarget = null;
        if (metadata is FileMetadata fileMetadata)
        {
            byte[] content = await pass.Files.TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) ?? [];
            contentHash = ComputeHash(content);
            length = fileMetadata.Length;
        }
        else if (metadata is SymbolicLinkMetadata linkMetadata)
        {
            symlinkTarget = linkMetadata.Target;
        }

        FileSystemSyncPayload payload = new(
            GetRelativePath(pass.Home, path),
            metadata.Kind.ToString(),
            metadata.OwnerId,
            metadata.GroupId,
            metadata.Permissions.Mode,
            metadata.Timestamps.CreatedAtUtc,
            metadata.Timestamps.ContentModifiedAtUtc,
            metadata.Timestamps.MetadataChangedAtUtc,
            contentHash,
            length,
            symlinkTarget);
        string payloadJson = JsonSerializer.Serialize(payload, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload);
        string hash = ComputeHash(Encoding.UTF8.GetBytes(payloadJson));
        string changeHash = metadata.Kind == FileSystemEntryKind.Directory
            ? ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                payload with { ContentModifiedAtUtc = payload.CreatedAtUtc, MetadataChangedAtUtc = payload.CreatedAtUtc },
                FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)))
            : hash;
        return new LocalDescription(payload, payloadJson, hash, changeHash);
    }

    private static SyncFileVersion DescribeRemote(SyncRecordEnvelope envelope)
    {
        if (envelope.IsTombstone || envelope.PayloadJson is null)
        {
            return new SyncFileVersion(true, envelope.ModifiedUtc, null, null, envelope.OriginDeviceId);
        }

        FileSystemSyncPayload payload = JsonSerializer.Deserialize(
            envelope.PayloadJson, FileSystemSyncContractsJsonContext.Default.FileSystemSyncPayload)!;
        return new SyncFileVersion(false, payload.ContentModifiedAtUtc, payload.Length, payload.ContentHash, envelope.OriginDeviceId);
    }

    /// <summary>Listing of the home: every entry, and the directories that could not be listed.</summary>
    private sealed record WalkResult(
        bool RootListed,
        List<(VirtualPath Path, FileSystemEntryMetadata Metadata)> Entries,
        List<string> UnlistedDirectories);

    private async Task<WalkResult> WalkAsync(PassContext pass, CancellationToken cancellationToken)
    {
        List<(VirtualPath, FileSystemEntryMetadata)> results = [];
        List<string> unlisted = [];
        bool rootListed = false;
        Queue<VirtualPath> directories = new();
        directories.Enqueue(pass.Home);

        while (directories.Count > 0)
        {
            VirtualPath directory = directories.Dequeue();
            FileSystemResult<FileSystemDirectorySnapshot> enumerated = await _fileSystem.EnumerateAsync(
                new FileSystemEnumerateRequest(directory), pass.Authorization, cancellationToken).ConfigureAwait(false);
            if (!enumerated.Succeeded)
            {
                if (directory != pass.Home)
                {
                    unlisted.Add(GetRelativePath(pass.Home, directory));
                }

                continue; // e.g. the home directory hasn't been seeded on this device yet.
            }

            rootListed |= directory == pass.Home;
            foreach (FileSystemDirectoryItem item in enumerated.Value!.Entries)
            {
                VirtualPath childPath = VirtualPath.Parse($"{directory.Value}/{item.Name.Value}");
                if (IsDeviceLocal(GetRelativePath(pass.Home, childPath)))
                {
                    continue;
                }

                results.Add((childPath, item.Metadata));
                if (item.Metadata.Kind == FileSystemEntryKind.Directory)
                {
                    directories.Enqueue(childPath);
                }
            }
        }

        return new WalkResult(rootListed, results, unlisted);
    }

    private async Task UploadContentAsync(PassContext pass, string contentHash, byte[] content, CancellationToken cancellationToken)
    {
        InitiateContentUploadResponse initiate = await _contentClient.InitiateUploadAsync(
            pass.ServerUrl, pass.AccessToken, new InitiateContentUploadRequest(contentHash, content.Length, pass.Server!.AccountId), cancellationToken)
            .ConfigureAwait(false);
        if (initiate.AlreadyExists)
        {
            return; // Dedup short-circuit — the server already has this content.
        }

        for (int index = 0; index < initiate.TotalChunks; index++)
        {
            int start = index * initiate.ChunkSizeBytes;
            int length = Math.Min(initiate.ChunkSizeBytes, content.Length - start);
            byte[] chunk = content[start..(start + length)];
            await _contentClient.UploadChunkAsync(pass.ServerUrl, pass.AccessToken, initiate.UploadSessionId, index, chunk, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<byte[]> DownloadContentAsync(PassContext pass, string contentHash, CancellationToken cancellationToken)
    {
        InitiateContentDownloadResponse initiate = await _contentClient.InitiateDownloadAsync(
            pass.ServerUrl, pass.AccessToken, new InitiateContentDownloadRequest(contentHash, StartByteOffset: 0), cancellationToken)
            .ConfigureAwait(false);

        using MemoryStream buffer = new();
        int totalChunks = initiate.TotalBytes == 0
            ? 0
            : (int)Math.Ceiling((double)initiate.TotalBytes / initiate.ChunkSizeBytes);
        for (int index = 0; index < totalChunks; index++)
        {
            byte[] chunk = await _contentClient.DownloadChunkAsync(pass.ServerUrl, pass.AccessToken, contentHash, index, cancellationToken)
                .ConfigureAwait(false);
            buffer.Write(chunk);
        }

        return buffer.ToArray();
    }

    /// <summary>Decodes the start of a file as text, or <see langword="null"/> when it is not UTF-8 text.</summary>
    private static string? ToPreviewText(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        ReadOnlySpan<byte> head = bytes.AsSpan(0, Math.Min(bytes.Length, PreviewCharacters * 4));
        if (head.Contains((byte)0))
        {
            return null; // A NUL byte: binary content, no preview.
        }

        try
        {
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(
                head.Length < bytes.Length ? TrimIncompleteUtf8(head) : head);
            return text.Length > PreviewCharacters ? text[..PreviewCharacters] : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Drops a multi-byte character cut by the preview window.</summary>
    private static ReadOnlySpan<byte> TrimIncompleteUtf8(ReadOnlySpan<byte> head)
    {
        int end = head.Length;
        int continuation = 0;
        while (end > 0 && continuation < 3 && (head[end - 1] & 0b1100_0000) == 0b1000_0000)
        {
            end--;
            continuation++;
        }

        return end > 0 && head[end - 1] >= 0b1100_0000 ? head[..(end - 1)] : head;
    }

    private static FileSystemAuthorizationContext CreateAuthorizationContext(AuthenticatedPrincipal principal)
    {
        // Acts as the real logged-in user, not a system-elevated context: FileSystem sync only ever
        // touches that user's own home directory, which they already own outright (per
        // FileSystemAuthorizer's owner-match rule), so no elevated authority is needed or wanted.
        AppOperationContext operation = new()
        {
            AppId = "org.hackeros.sync",
            UserId = principal.LoginName.Value,
            UserAuthority = principal.Authority,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = false
        };
        return new FileSystemAuthorizationContext(
            operation,
            principal.GroupIds.Select(group => group.ToString()),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets whether a home-relative path is device-local state excluded from sync:
    /// <c>.local/state</c> and everything below it (ADR 0044 section 10).
    /// </summary>
    public static bool IsDeviceLocal(string relativePath) =>
        relativePath == DeviceLocalStateRoot
        || relativePath.StartsWith($"{DeviceLocalStateRoot}/", StringComparison.Ordinal);

    private const string DeviceLocalStateRoot = ".local/state";

    private static int Depth(string relativePath) => relativePath.Count(c => c == '/');

    private static VirtualPath ToAbsolute(PassContext pass, string relativePath) =>
        VirtualPath.Parse($"{pass.Home.Value}/{relativePath}");

    private static string GetRelativePath(VirtualPath homePath, VirtualPath path) =>
        path.Value[(homePath.Value.Length + 1)..];

    private static string ComputeHash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
