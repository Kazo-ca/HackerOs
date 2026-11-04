using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.Tests.ServerConnection;

/// <summary>
/// ADR 0101 end to end: two simulated devices of the same user synchronize their home through an
/// <see cref="InMemorySyncServer"/> with the server's rules. Covers propagated deletions, received
/// tombstones landing in the trash, renames, the three conflict choices, the frozen file, the
/// reload of pending conflicts and the full resynchronization after more than 90 days offline.
/// </summary>
public sealed class FileSystemSyncDeletionAndConflictTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private readonly InMemorySyncServer _server = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_deletion_travels_as_a_tombstone_and_lands_in_the_other_devices_trash()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "hello");
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("hello", await b.ReadAsync("note.txt"));

        await a.DeleteAsync("note.txt");
        SyncPassResult pushed = await a.Service.PushAsync();
        Assert.Equal(1, pushed.Sent);
        Guid recordId = a.RecordIdOf("note.txt");
        Assert.True(_server.Latest(recordId)!.IsTombstone);

        await b.SyncAsync();

        Assert.False(await b.ExistsAsync("note.txt"));
        Assert.Equal("hello", await b.ReadAsync(".local/share/Trash/files/note.txt"));
        string info = await b.ReadAsync(".local/share/Trash/info/note.txt.trashinfo");
        Assert.Contains("Path=/home/alice/note.txt", info, StringComparison.Ordinal);
        Assert.Contains("X-HackerOS-Origin=deleted-on-another-device", info, StringComparison.Ordinal);

        // The copy the sync trashed on B is local only: it never comes back to A.
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.False(await a.ExistsAsync(".local/share/Trash/files/note.txt"));
    }

    [Fact]
    public async Task An_entry_never_synchronized_leaves_no_tombstone()
    {
        Device a = new(_server, _time);
        await a.SyncAsync();
        await a.WriteAsync("draft.txt", "local only");
        await a.DeleteAsync("draft.txt");

        SyncPassResult pushed = await a.Service.PushAsync();

        Assert.Equal(0, pushed.Sent);
    }

    [Fact]
    public async Task A_pulled_file_is_neither_sent_back_nor_mistaken_for_a_deletion()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "hello");
        await a.SyncAsync();
        await b.SyncAsync();

        SyncPassResult again = await b.Service.PushAsync();

        Assert.Equal(0, again.Sent);
        Assert.False(_server.Latest(a.RecordIdOf("note.txt"))!.IsTombstone);
    }

    [Fact]
    public async Task A_rename_moves_the_file_on_the_other_device()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "hello");
        await a.SyncAsync();
        await b.SyncAsync();

        await a.MoveAsync("note.txt", "Documents/renamed.txt");
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.False(await b.ExistsAsync("note.txt"));
        Assert.Equal("hello", await b.ReadAsync("Documents/renamed.txt"));
    }

    [Fact]
    public async Task Emptying_the_trash_deletes_the_trashed_copy_on_the_other_device()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "hello");
        await a.SyncAsync();
        await b.SyncAsync();

        // Trashing is a synchronized rename (ADR 0052), not a deletion.
        await a.MoveAsync("note.txt", ".local/share/Trash/files/note.txt");
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("hello", await b.ReadAsync(".local/share/Trash/files/note.txt"));

        await a.DeleteAsync(".local/share/Trash/files/note.txt");
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.False(await b.ExistsAsync(".local/share/Trash/files/note.txt"));
    }

    [Fact]
    public async Task A_concurrent_edit_is_frozen_until_the_user_answers_and_survives_a_reload()
    {
        (Device a, Device b) = await TwoDevicesWithConflictAsync();

        IReadOnlyList<SyncFileConflict> conflicts = await b.Service.GetConflictsAsync();
        SyncFileConflict conflict = Assert.Single(conflicts);
        Assert.Equal("note.txt", conflict.RelativePath);
        Assert.NotNull(conflict.Remote);
        Assert.Equal("from B", await b.ReadAsync("note.txt"));
        Assert.True(await b.ExistsAsync(".local/state/hackeros/sync-conflicts.json"));

        // Frozen: a new local edit is not sent, a new remote edit is not applied.
        await b.WriteAsync("note.txt", "from B again");
        Assert.Equal(0, (await b.Service.PushAsync()).Sent);
        await a.WriteAsync("note.txt", "from A again");
        await a.SyncAsync();
        await b.Service.PullAsync();
        Assert.Equal("from B again", await b.ReadAsync("note.txt"));

        // A reload (new service instance, same files and IndexedDB) still knows the conflict.
        FileSystemSyncService reloaded = b.CreateService();
        SyncFileConflict reloadedConflict = Assert.Single(await reloaded.GetConflictsAsync());
        SyncConflictPreview preview = await reloaded.GetConflictPreviewAsync(reloadedConflict.RecordId);
        Assert.Equal("from B again", preview.LocalText);
        Assert.Equal("from A again", preview.RemoteText);
    }

    [Fact]
    public async Task Keep_mine_sends_this_devices_version_to_the_other_device()
    {
        (Device a, Device b) = await TwoDevicesWithConflictAsync();
        Guid recordId = Assert.Single(await b.Service.GetConflictsAsync()).RecordId;

        await b.Service.ResolveConflictAsync(recordId, SyncConflictChoice.KeepMine);
        await a.SyncAsync();

        Assert.Empty(await b.Service.GetConflictsAsync());
        Assert.Equal(ConflictResolution.ClientWins, _server.Resolutions[^1].Resolution);
        Assert.Equal("from B", await a.ReadAsync("note.txt"));
        Assert.Equal(0, (await b.Service.PushAsync()).Sent);
    }

    [Fact]
    public async Task Keep_other_takes_the_server_version_and_trashes_the_local_one()
    {
        (_, Device b) = await TwoDevicesWithConflictAsync();
        Guid recordId = Assert.Single(await b.Service.GetConflictsAsync()).RecordId;

        await b.Service.ResolveConflictAsync(recordId, SyncConflictChoice.KeepOther);

        Assert.Equal("from A", await b.ReadAsync("note.txt"));
        Assert.Equal("from B", await b.ReadAsync(".local/share/Trash/files/note.txt"));

        // Only the new trash directories go out: neither version of the file comes back.
        int before = _server.Rows.Count;
        await b.Service.PushAsync();
        Assert.All(_server.Rows.Skip(before), row => Assert.Contains("\"kind\":\"Directory\"", row.PayloadJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keep_both_renames_the_local_version_and_sends_it_as_a_new_file()
    {
        (Device a, Device b) = await TwoDevicesWithConflictAsync();
        Guid recordId = Assert.Single(await b.Service.GetConflictsAsync()).RecordId;

        await b.Service.ResolveConflictAsync(recordId, SyncConflictChoice.KeepBoth);

        Assert.Equal("from A", await b.ReadAsync("note.txt"));
        string copyName = Assert.Single(await b.ListAsync(string.Empty), name => name.StartsWith("note.sync-conflict-", StringComparison.Ordinal));
        Assert.Matches(@"^note\.sync-conflict-20261017-\d{6}-[0-9a-f]{8}\.txt$", copyName);
        Assert.Equal("from B", await b.ReadAsync(copyName));

        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal("from B", await a.ReadAsync(copyName));
        Assert.Equal("from A", await a.ReadAsync("note.txt"));
    }

    [Fact]
    public async Task Modified_here_and_deleted_there_asks_and_keep_other_trashes_the_local_file()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "v1");
        await a.SyncAsync();
        await b.SyncAsync();

        await a.DeleteAsync("note.txt");
        await a.SyncAsync();
        await b.WriteAsync("note.txt", "edited on B");
        await b.SyncAsync();

        SyncFileConflict conflict = Assert.Single(await b.Service.GetConflictsAsync());
        Assert.True(conflict.Remote!.IsDeleted);
        Assert.Equal("edited on B", await b.ReadAsync("note.txt"));

        await b.Service.ResolveConflictAsync(conflict.RecordId, SyncConflictChoice.KeepOther);

        Assert.False(await b.ExistsAsync("note.txt"));
        Assert.Equal("edited on B", await b.ReadAsync(".local/share/Trash/files/note.txt"));
    }

    [Fact]
    public async Task The_same_edit_on_both_devices_resolves_itself()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "v1");
        await a.SyncAsync();
        await b.SyncAsync();

        await a.WriteAsync("note.txt", "same");
        await a.SyncAsync();
        await b.WriteAsync("note.txt", "same");
        await b.SyncAsync();

        Assert.Empty(await b.Service.GetConflictsAsync());
        Assert.Equal(0, (await b.Service.PushAsync()).Sent);
    }

    [Fact]
    public async Task A_device_offline_more_than_90_days_resynchronizes_fully_and_applies_purged_deletions()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "hello");
        await a.WriteAsync("kept.txt", "kept");
        await a.SyncAsync();
        await b.SyncAsync();

        await a.DeleteAsync("note.txt");
        await a.SyncAsync();
        _time.Now += TimeSpan.FromDays(91);
        _server.Purge(a.RecordIdOf("note.txt")); // The server forgot the tombstone.

        await b.Service.PullAsync();

        Assert.False(await b.ExistsAsync("note.txt"));
        Assert.Equal("hello", await b.ReadAsync(".local/share/Trash/files/note.txt"));
        Assert.Equal("kept", await b.ReadAsync("kept.txt"));
    }

    [Fact]
    public async Task Conflict_copy_names_keep_the_extension_last()
    {
        Assert.Equal("report.sync-conflict-20261017-081500-ab12cd34.txt",
            FileSystemSyncService.BuildConflictCopyName("report.txt", "20261017-081500", "ab12cd34"));
        Assert.Equal(".bashrc.sync-conflict-20261017-081500-ab12cd34",
            FileSystemSyncService.BuildConflictCopyName(".bashrc", "20261017-081500", "ab12cd34"));
        await Task.CompletedTask;
    }

    /// <summary>Both devices change <c>note.txt</c>; A syncs first, so B's push conflicts.</summary>
    [Fact]
    public async Task The_same_file_created_on_both_devices_is_kept_once()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("Documents/same.txt", "identical");
        await b.WriteAsync("Documents/same.txt", "identical");

        await ConvergeAsync(a, b);

        Assert.Equal("identical", await a.ReadAsync("Documents/same.txt"));
        Assert.Equal("identical", await b.ReadAsync("Documents/same.txt"));
        Assert.False(await a.ExistsAsync(".local/share/Trash/files/same.txt"));
        Assert.False(await b.ExistsAsync(".local/share/Trash/files/same.txt"));
    }

    [Fact]
    public async Task Two_different_files_created_at_the_same_path_ask_the_user()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("plan.txt", "plan A");
        await b.WriteAsync("plan.txt", "plan B");

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        // Whichever device adopted the other record holds the conflict; nothing was overwritten.
        IReadOnlyList<SyncFileConflict> conflicts = [.. await a.Service.GetConflictsAsync(), .. await b.Service.GetConflictsAsync()];
        SyncFileConflict conflict = Assert.Single(conflicts);
        Assert.Equal("plan.txt", conflict.RelativePath);
        Assert.Equal("plan A", await a.ReadAsync("plan.txt"));
        Assert.Equal("plan B", await b.ReadAsync("plan.txt"));
    }

    /// <summary>
    /// Lets two fresh devices settle the standard directories both seeded at first login (each
    /// created them under its own ids; the smallest record id keeps each path).
    /// </summary>
    private static async Task ConvergeAsync(Device a, Device b)
    {
        for (int round = 0; round < 3; round++)
        {
            await a.SyncAsync();
            await b.SyncAsync();
        }

        Assert.Equal(0, (await a.Service.PushAsync()).Sent);
        Assert.Equal(0, (await b.Service.PushAsync()).Sent);
        Assert.Empty(await a.Service.GetConflictsAsync());
        Assert.Empty(await b.Service.GetConflictsAsync());
    }

    private async Task<(Device A, Device B)> TwoDevicesWithConflictAsync()
    {
        Device a = new(_server, _time);
        Device b = new(_server, _time);
        await ConvergeAsync(a, b);
        await a.WriteAsync("note.txt", "v1");
        await a.SyncAsync();
        await b.SyncAsync();

        await a.WriteAsync("note.txt", "from A");
        await a.SyncAsync();
        await b.WriteAsync("note.txt", "from B");
        SyncPassResult push = await b.Service.PushAsync();
        Assert.Equal(1, push.Conflicts);
        await b.Service.PullAsync();
        return (a, b);
    }

    /// <summary>One simulated device: its own filesystem, IndexedDB-like stores and server link.</summary>
    private sealed class Device
    {
        private readonly FileSystemService _fileSystem;
        private readonly InMemorySyncServer _server;
        private readonly TimeProvider _time;
        private readonly InMemorySyncCursorRepository _cursors = new();
        private readonly InMemorySyncRecordStateRepository _states = new();
        private readonly ServerConnectionState _connection;
        private readonly SessionStub _session;
        private int _entryId = 1;
        private int _transactionId = 100;

        internal Device(InMemorySyncServer server, TimeProvider time)
        {
            _server = server;
            _time = time;
            Guid seed = Guid.NewGuid();
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, (short)(seed.GetHashCode() & 0x7FFF), 0, seed.ToByteArray()[..8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                TimeProvider.System);
            FileSystemMountRouter router = new(repository);
            _fileSystem = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository()),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));

            LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
            Principal = new AuthenticatedPrincipal(
                SessionId.FromGuid(Guid.NewGuid()),
                LocalUserId.FromGuid(Guid.NewGuid()),
                LocalLoginName.Parse("alice"),
                "Alice",
                AppAuthority.User,
                group,
                [group],
                InstallationId.FromGuid(Guid.NewGuid()),
                DeviceId.FromGuid(Guid.NewGuid()),
                DateTimeOffset.UtcNow);
            _session = new SessionStub(Principal);
            _connection = new ServerConnectionState(
                AccountId, Guid.NewGuid(), "https://sync.test/", "fingerprint", "refresh", DateTimeOffset.UtcNow.AddDays(365));
            new FileSystemSeeder(_fileSystem).SeedAsync("alice", group.ToString()).AsTask().GetAwaiter().GetResult();
            Service = CreateService();
        }

        internal AuthenticatedPrincipal Principal { get; }

        internal FileSystemSyncService Service { get; }

        internal FileSystemSyncService CreateService() => new(
            _fileSystem,
            _session,
            new ConnectionStub(_connection),
            _server,
            _server,
            _cursors,
            _states,
            _time);

        internal async Task SyncAsync()
        {
            await Service.PushAsync();
            await Service.PullAsync();
        }

        internal Guid RecordIdOf(string relativePath) =>
            _states.ListAsync(SyncDomain.FileSystem).AsTask().GetAwaiter().GetResult()
                .Single(record => record.State.RelativePath == relativePath).RecordId;

        internal async Task WriteAsync(string relativePath, string text)
        {
            VirtualPath path = Absolute(relativePath);
            await _fileSystem.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)), Context());
            FileSystemMutationResult written = await _fileSystem.WriteAsync(
                new FileSystemWriteRequest(path), new SyncFileOperations.ByteContentSource(Encoding.UTF8.GetBytes(text)), Context());
            Assert.True(written.Succeeded, $"write {relativePath}: {written.Transaction.Error?.Code}");
        }

        internal async Task<string> ReadAsync(string relativePath)
        {
            FileSystemResult<FileSystemContentReadHandle> result = await _fileSystem.ReadAsync(
                new FileSystemReadRequest(Absolute(relativePath)), Context());
            Assert.True(result.Succeeded, $"read {relativePath}: {result.Error?.Code}");
            await using FileSystemContentReadHandle handle = result.Value!;
            using StreamReader reader = new(handle.Content);
            return await reader.ReadToEndAsync();
        }

        internal async Task<bool> ExistsAsync(string relativePath) =>
            (await _fileSystem.StatAsync(new FileSystemStatRequest(Absolute(relativePath)), Context())).Succeeded;

        internal async Task<IReadOnlyList<string>> ListAsync(string relativeDirectory)
        {
            VirtualPath directory = relativeDirectory.Length == 0 ? Absolute(null) : Absolute(relativeDirectory);
            FileSystemResult<FileSystemDirectorySnapshot> listed = await _fileSystem.EnumerateAsync(
                new FileSystemEnumerateRequest(directory), Context());
            return [.. listed.Value!.Entries.Select(entry => entry.Name.Value)];
        }

        internal async Task DeleteAsync(string relativePath) =>
            await new SyncFileOperations(_fileSystem, Context()).DeleteRecursiveAsync(Absolute(relativePath), CancellationToken.None);

        internal async Task MoveAsync(string from, string to) =>
            await new SyncFileOperations(_fileSystem, Context()).MoveAsync(Absolute(from), Absolute(to), CancellationToken.None);

        private static VirtualPath Absolute(string? relativePath) =>
            VirtualPath.Parse(relativePath is null ? "/home/alice" : $"/home/alice/{relativePath}");

        private FileSystemAuthorizationContext Context() => new(
            new AppOperationContext
            {
                AppId = "test",
                UserId = "alice",
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = false
            },
            Principal.GroupIds.Select(id => id.ToString()),
            DateTimeOffset.UtcNow);
    }

    private sealed class SessionStub(AuthenticatedPrincipal principal) : ISessionService
    {
        public SessionState State => SessionState.Active;

        public AuthenticatedPrincipal? CurrentPrincipal => principal;

        public Task<AuthenticatedPrincipal> LoginAsync(LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public CancellationTokenSource CreateLinkedCancellationSource() => throw new NotSupportedException();
    }

    private sealed class ConnectionStub(ServerConnectionState state) : IServerConnectionService
    {
        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ServerConnectionState?>(state);

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("token");
    }
}
