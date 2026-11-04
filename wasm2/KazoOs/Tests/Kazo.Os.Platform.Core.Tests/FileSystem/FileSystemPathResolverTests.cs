using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemPathResolverTests
{
    [Fact]
    public async Task Relative_link_resolves_from_parent_and_reenters_mount_routing()
    {
        MetadataProvider root = new("root");
        MetadataProvider mounted = new("mounted");
        root.AddDirectory("/home");
        root.AddDirectory("/home/user");
        root.AddDirectory("/etc");
        root.AddLink("/home/user/config", "/etc/hackeros/settings.json");
        mounted.AddDirectory("/etc");
        mounted.AddDirectory("/etc/hackeros");
        mounted.AddFile("/etc/hackeros/settings.json");
        FileSystemPathResolver resolver = CreateResolver(root, mounted);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/home/user/config"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.True(result.Succeeded);
        Assert.Equal("/etc/hackeros/settings.json", result.Value?.Path.Value);
        Assert.Equal(1, result.Value?.FollowedSymbolicLinks);
        Assert.Contains("/etc/hackeros/settings.json", mounted.StatPaths);
    }

    [Fact]
    public async Task No_follow_preserves_the_final_link_for_delete_semantics()
    {
        MetadataProvider root = new("root");
        root.AddDirectory("/home");
        root.AddLink("/home/link", "/target");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/home/link"),
            FileSystemLinkBehavior.NoFollow,
            FileSystemOperation.Delete,
            CreateContext());

        Assert.True(result.Succeeded);
        Assert.Equal("/home/link", result.Value?.Path.Value);
        Assert.Equal(0, result.Value?.FollowedSymbolicLinks);
    }

    [Fact]
    public async Task Repeated_link_identity_returns_loop_error()
    {
        MetadataProvider root = new("root");
        root.AddLink("/a", "/b", "15f88b8c98a4479d9463d68867d35e15");
        root.AddLink("/b", "/a", "6fa459eaee8a3ca4894e0db77e160355");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/a"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.Equal(FileSystemErrorCode.SymbolicLinkLoop, result.Error?.Code);
    }

    [Fact]
    public async Task More_than_forty_distinct_links_returns_limit_error()
    {
        MetadataProvider root = new("root");
        for (int index = 0; index <= FileSystemTraversalPolicy.MaximumSymbolicLinkHops; index++)
        {
            root.AddLink(
                $"/link-{index}",
                index == FileSystemTraversalPolicy.MaximumSymbolicLinkHops
                    ? "/target"
                    : $"/link-{index + 1}");
        }

        root.AddFile("/target");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/link-0"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.Equal(FileSystemErrorCode.SymbolicLinkLimitExceeded, result.Error?.Code);
    }

    [Fact]
    public async Task Relative_link_cannot_escape_virtual_root()
    {
        MetadataProvider root = new("root");
        root.AddDirectory("/home");
        root.AddLink("/home/link", "../../outside");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/home/link"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.Equal(FileSystemErrorCode.RootContainmentViolation, result.Error?.Code);
    }

    [Fact]
    public async Task Intermediate_directory_requires_execute_permission()
    {
        MetadataProvider root = new("root");
        root.AddDirectory("/private", owner: "other", mode: 0x01C0);
        root.AddFile("/private/file.txt");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/private/file.txt"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.Equal(FileSystemErrorCode.PermissionDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Walking_provider_resolves_a_deep_path_in_one_request()
    {
        WalkingMetadataProvider root = new("root");
        root.AddDirectory("/home");
        root.AddDirectory("/home/user");
        root.AddDirectory("/home/user/.config");
        root.AddDirectory("/home/user/.config/hackeros");
        root.AddFile("/home/user/.config/hackeros/ui-platform.config");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/home/user/.config/hackeros/ui-platform.config"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Write,
            CreateContext());

        Assert.True(result.Succeeded);
        Assert.Equal("/home/user/.config/hackeros/ui-platform.config", result.Value?.Path.Value);
        Assert.Equal(["/home/user/.config/hackeros/ui-platform.config"], root.WalkPaths);
        Assert.Empty(root.StatPaths);
    }

    [Fact]
    public async Task Walking_provider_reports_the_first_missing_component()
    {
        WalkingMetadataProvider root = new("root");
        root.AddDirectory("/home");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/home/user/.config/file"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.Equal(FileSystemErrorCode.NotFound, result.Error?.Code);
        Assert.Equal("/home/user", result.Error?.Path?.Value);
    }

    [Fact]
    public async Task Walking_provider_keeps_traverse_permission_and_link_restarts()
    {
        WalkingMetadataProvider root = new("root");
        root.AddDirectory("/private", owner: "other", mode: 0x01C0);
        root.AddFile("/private/file.txt");
        root.AddDirectory("/home");
        root.AddLink("/home/docs", "/srv/docs");
        root.AddDirectory("/srv");
        root.AddDirectory("/srv/docs");
        root.AddFile("/srv/docs/readme.txt");
        FileSystemPathResolver resolver = CreateResolver(root);

        FileSystemResult<FileSystemPathResolution> denied = await resolver.ResolveAsync(
            VirtualPath.Parse("/private/file.txt"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());
        FileSystemResult<FileSystemPathResolution> followed = await resolver.ResolveAsync(
            VirtualPath.Parse("/home/docs/readme.txt"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.Equal(FileSystemErrorCode.PermissionDenied, denied.Error?.Code);
        Assert.Equal("/srv/docs/readme.txt", followed.Value?.Path.Value);
        Assert.Equal(1, followed.Value?.FollowedSymbolicLinks);
        // The walk stops at the link (a link has no children); the resolver restarts on the target.
        Assert.Equal(["/private/file.txt", "/home/docs/readme.txt", "/srv/docs/readme.txt"], root.WalkPaths);
    }

    [Fact]
    public async Task Path_crossing_a_mount_is_stated_per_prefix()
    {
        WalkingMetadataProvider root = new("root");
        MetadataProvider mounted = new("mounted");
        root.AddDirectory("/etc");
        mounted.AddDirectory("/etc/hackeros");
        mounted.AddFile("/etc/hackeros/settings.json");
        FileSystemPathResolver resolver = CreateResolver(root, mounted);

        FileSystemResult<FileSystemPathResolution> result = await resolver.ResolveAsync(
            VirtualPath.Parse("/etc/hackeros/settings.json"),
            FileSystemLinkBehavior.Follow,
            FileSystemOperation.Read,
            CreateContext());

        Assert.True(result.Succeeded);
        Assert.Empty(root.WalkPaths);
        Assert.Equal(["/etc"], root.StatPaths);
    }

    [Fact]
    public void Virtual_path_normalizes_unicode_and_enforces_encoded_limits()
    {
        VirtualPath normalized = VirtualPath.Parse("/home/re\u0301sume\u0301.txt");

        Assert.Equal("/home/résumé.txt", normalized.Value);
        Assert.Throws<FormatException>(() => VirtualPath.Parse($"/{new string('é', 128)}"));
        Assert.Throws<FormatException>(() => VirtualPath.Parse("~"));
    }

    private static FileSystemPathResolver CreateResolver(
        MetadataProvider root,
        MetadataProvider? mounted = null) =>
        new(new FileSystemMountRouter(
            root,
            mounted is null
                ? null
                : [new FileSystemMount(VirtualPath.Parse("/etc/hackeros"), mounted)]));

    private static FileSystemAuthorizationContext CreateContext()
    {
        AppOperationContext operationContext = new()
        {
            AppId = "org.hackeros.test",
            UserId = "user",
            UserAuthority = AppAuthority.User,
            GrantedCapabilities = new HashSet<string>(StringComparer.Ordinal)
        };
        return new FileSystemAuthorizationContext(
            operationContext,
            ["users"],
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
    }

    private class MetadataProvider(string providerId) : IFileSystemProvider
    {
        private readonly Dictionary<string, FileSystemEntryMetadata> _entries =
            new(StringComparer.Ordinal);
        private int _nextId = 1;

        public string ProviderId { get; } = providerId;

        public List<string> StatPaths { get; } = [];

        public void AddDirectory(
            string path,
            string owner = "user",
            ushort mode = 0x01FF) =>
            Add(path, FileSystemEntryKind.Directory, owner: owner, mode: mode);

        public void AddFile(string path) => Add(path, FileSystemEntryKind.File);

        public void AddLink(string path, string target, string? id = null) =>
            Add(path, FileSystemEntryKind.SymbolicLink, target, id);

        protected FileSystemEntryMetadata? Find(string path) =>
            _entries.TryGetValue(path, out FileSystemEntryMetadata? metadata) ? metadata : null;

        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
            FileSystemStatRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default)
        {
            StatPaths.Add(request.Path.Value);
            return ValueTask.FromResult(_entries.TryGetValue(request.Path.Value, out FileSystemEntryMetadata? metadata)
                ? FileSystemResult<FileSystemEntrySnapshot>.Success(
                    new FileSystemEntrySnapshot(request.Path, metadata))
                : FileSystemResult<FileSystemEntrySnapshot>.Failure(
                    new FileSystemError(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, request.Path)));
        }

        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
            FileSystemReadRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
            FileSystemEnumerateRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> CreateAsync(FileSystemCreateRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> WriteAsync(FileSystemWriteRequest request, IFileSystemContentSource content, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private void Add(
            string path,
            FileSystemEntryKind kind,
            string? target = null,
            string? id = null,
            string owner = "user",
            ushort mode = 0x01FF)
        {
            DateTimeOffset timestamp = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
            FileSystemEntryId entryId = id is null
                ? FileSystemEntryId.FromGuid(new Guid(_nextId++, 0, 0, new byte[8]))
                : FileSystemEntryId.Parse(id);
            FileSystemTimestamps timestamps = new(timestamp, timestamp, timestamp);
            FileSystemPermissions permissions = FileSystemPermissions.FromMode(mode);
            _entries[VirtualPath.Parse(path).Value] = kind switch
            {
                FileSystemEntryKind.Directory => new DirectoryMetadata(entryId, owner, "users", permissions, timestamps, 1),
                FileSystemEntryKind.File => new FileMetadata(entryId, owner, "users", permissions, timestamps, 1, 0),
                FileSystemEntryKind.SymbolicLink => new SymbolicLinkMetadata(entryId, owner, "users", permissions, timestamps, 1, target!),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
    }

    /// <summary>A provider that looks a whole path up in one request, like the IndexedDB one.</summary>
    private sealed class WalkingMetadataProvider(string providerId) : MetadataProvider(providerId), IFileSystemPathWalkProvider
    {
        public List<string> WalkPaths { get; } = [];

        public ValueTask<FileSystemResult<IReadOnlyList<FileSystemEntrySnapshot>>> StatComponentsAsync(
            VirtualPath path,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default)
        {
            WalkPaths.Add(path.Value);
            string[] names = path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            List<FileSystemEntrySnapshot> components = [];
            for (int count = 1; count <= names.Length; count++)
            {
                VirtualPath prefix = VirtualPath.Parse($"/{string.Join('/', names.Take(count))}");
                if (Find(prefix.Value) is not { } metadata)
                {
                    break;
                }

                components.Add(new FileSystemEntrySnapshot(prefix, metadata));
            }

            return ValueTask.FromResult(FileSystemResult<IReadOnlyList<FileSystemEntrySnapshot>>.Success(components));
        }
    }
}
