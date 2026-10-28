using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.FileSystem.HostFolders;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Tests.FileSystem.HostFolders;

/// <summary>The <c>hostfs</c> provider (ADR 0181, section 2) over an in-memory host folder.</summary>
public sealed class HostFolderFileSystemProviderTests
{
    private static readonly VirtualPath MountPath = VirtualPath.Parse("/mnt/work");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Move_renames_in_one_call_or_copies_then_deletes(bool browserCanMove)
    {
        FakeHostFolder folder = new("work") { CanMove = browserCanMove };
        folder.Put("one", "docs", "a.txt");
        folder.Put("two", "docs", "b.txt");
        HostFolderFileSystemProvider provider = Provider(folder);

        FileSystemMutationResult moved = await provider.MoveAsync(
            new FileSystemMoveRequest(
                Path("docs"),
                Path("archive"),
                await RevisionAsync(provider, "docs"),
                await RevisionAsync(provider, null),
                await RevisionAsync(provider, null)),
            Context());

        Assert.True(moved.Succeeded, $"move: {moved.Transaction.Error?.Code}");
        Assert.False(folder.Exists("docs"));
        Assert.Equal("one", folder.Text("archive", "a.txt"));
        Assert.Equal("two", folder.Text("archive", "b.txt"));
    }

    [Fact]
    public async Task A_stale_revision_is_a_conflict_and_changes_nothing()
    {
        FakeHostFolder folder = new("work");
        folder.Put("v1", "a.txt");
        HostFolderFileSystemProvider provider = Provider(folder);
        long before = await RevisionAsync(provider, "a.txt");
        folder.Put("changed on the computer", "a.txt");

        FileSystemMutationResult deleted = await provider.DeleteAsync(
            new FileSystemDeleteRequest(Path("a.txt"), before, await RevisionAsync(provider, null)),
            Context());

        Assert.False(deleted.Succeeded);
        Assert.Equal(FileSystemErrorCode.RevisionConflict, deleted.Transaction.Error!.Code);
        Assert.Equal("changed on the computer", folder.Text("a.txt"));
    }

    [Fact]
    public async Task Entries_belong_to_the_owner_with_private_modes_and_stable_ids()
    {
        FakeHostFolder folder = new("work");
        folder.Put("x", "sub", "file.txt");
        HostFolderFileSystemProvider provider = Provider(folder);

        FileSystemEntryMetadata file = (await provider.StatAsync(new FileSystemStatRequest(Path("sub/file.txt")), Context())).Value!.Metadata;
        FileSystemEntryMetadata dir = (await provider.StatAsync(new FileSystemStatRequest(Path("sub")), Context())).Value!.Metadata;
        FileSystemEntryMetadata again = (await provider.StatAsync(new FileSystemStatRequest(Path("sub/file.txt")), Context())).Value!.Metadata;

        Assert.Equal("alice", file.OwnerId);
        Assert.Equal(0x180, file.Permissions.Mode);
        Assert.Equal(0x1C0, dir.Permissions.Mode);
        Assert.Equal(file.Id, again.Id);
        Assert.NotEqual(file.Id, dir.Id);
    }

    [Fact]
    public async Task The_mount_root_cannot_be_deleted_and_permissions_cannot_be_changed()
    {
        FakeHostFolder folder = new("work");
        folder.Put("x", "a.txt");
        HostFolderFileSystemProvider provider = Provider(folder);
        long root = await RevisionAsync(provider, null);

        FileSystemMutationResult deleted = await provider.DeleteAsync(
            new FileSystemDeleteRequest(MountPath, root, root, recursive: true), Context());
        FileSystemMutationResult chmod = await provider.SetPermissionsAsync(
            new FileSystemSetPermissionsRequest(Path("a.txt"), FileSystemPermissions.FromMode(0x1FF), await RevisionAsync(provider, "a.txt")),
            Context());

        Assert.Equal(FileSystemErrorCode.ProtectedEntry, deleted.Transaction.Error!.Code);
        Assert.Equal(FileSystemErrorCode.UnsupportedOperation, chmod.Transaction.Error!.Code);
        Assert.Equal("x", folder.Text("a.txt"));
    }

    [Fact]
    public async Task A_disconnected_drive_shows_an_empty_root_and_refuses_the_rest()
    {
        FakeHostFolder folder = new("work");
        folder.Put("x", "a.txt");
        HostFolderFileSystemProvider provider = Provider(folder, connected: false);

        FileSystemResult<FileSystemEntrySnapshot> root = await provider.StatAsync(new FileSystemStatRequest(MountPath), Context());
        FileSystemResult<FileSystemContentReadHandle> read = await provider.ReadAsync(new FileSystemReadRequest(Path("a.txt")), Context());

        Assert.Equal(FileSystemEntryKind.Directory, root.Value!.Metadata.Kind);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, read.Error!.Code);
    }

    [Fact]
    public async Task A_revoked_permission_disconnects_the_drive()
    {
        FakeHostFolder folder = new("work");
        folder.Put("x", "a.txt");
        HostFolderFileSystemProvider provider = Provider(folder);
        folder.Permission = HostFolderPermission.Prompt;

        FileSystemResult<FileSystemContentReadHandle> read = await provider.ReadAsync(new FileSystemReadRequest(Path("a.txt")), Context());

        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, read.Error!.Code);
        Assert.False(provider.IsConnected);
    }

    [Fact]
    public async Task A_refused_entry_with_the_permission_still_granted_keeps_the_drive_connected()
    {
        RefusingFolder folder = new();
        folder.Put("x", "a.txt");
        HostFolderFileSystemProvider provider = Provider(folder);

        FileSystemResult<FileSystemContentReadHandle> read = await provider.ReadAsync(new FileSystemReadRequest(Path("a.txt")), Context());

        Assert.Equal(FileSystemErrorCode.PermissionDenied, read.Error!.Code);
        Assert.True(provider.IsConnected);
    }

    private static HostFolderFileSystemProvider Provider(IHostFolder folder, bool connected = true) =>
        new(MountPath, folder, "alice", "users", connected);

    private static VirtualPath Path(string relative) => VirtualPath.Parse($"{MountPath.Value}/{relative}");

    private static async Task<long> RevisionAsync(HostFolderFileSystemProvider provider, string? relative)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await provider.StatAsync(
            new FileSystemStatRequest(relative is null ? MountPath : Path(relative)), Context());
        Assert.True(stat.Succeeded, $"stat {relative}: {stat.Error?.Code}");
        return stat.Value!.Metadata.Revision;
    }

    private static FileSystemAuthorizationContext Context() => new(
        new AppOperationContext
        {
            AppId = "org.tests.hostfs",
            UserId = "alice",
            UserName = "alice",
            UserAuthority = AppAuthority.User,
            GrantedCapabilities = new HashSet<string>(StringComparer.Ordinal)
        },
        ["users"],
        DateTimeOffset.UnixEpoch);

    /// <summary>A folder whose browser refuses to read one file while the permission stands.</summary>
    private sealed class RefusingFolder : IHostFolder
    {
        private readonly FakeHostFolder _inner = new("work");

        public string Name => _inner.Name;

        public void Put(string text, params string[] path) => _inner.Put(text, path);

        public ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default) => _inner.QueryPermissionAsync(cancellationToken);
        public ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) => _inner.RequestPermissionAsync(cancellationToken);
        public ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) => _inner.StatAsync(path, cancellationToken);
        public ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) => _inner.ListAsync(path, cancellationToken);
        public ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "blocked");
        public ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default) => _inner.CreateAsync(path, kind, cancellationToken);
        public ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default) => _inner.WriteAsync(path, content, cancellationToken);
        public ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default) => _inner.DeleteAsync(path, recursive, cancellationToken);
        public ValueTask<bool> TryMoveAsync(IReadOnlyList<string> source, IReadOnlyList<string> destination, CancellationToken cancellationToken = default) => _inner.TryMoveAsync(source, destination, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
