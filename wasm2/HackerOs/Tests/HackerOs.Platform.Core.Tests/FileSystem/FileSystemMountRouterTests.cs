using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Tests.FileSystem;

public sealed class FileSystemMountRouterTests
{
    [Fact]
    public void Most_specific_segment_boundary_mount_wins()
    {
        StubProvider root = new("root");
        StubProvider settings = new("settings");
        StubProvider apps = new("app-settings");
        FileSystemMountRouter router = new(root,
        [
            new FileSystemMount(VirtualPath.Parse("/etc/hackeros"), settings),
            new FileSystemMount(VirtualPath.Parse("/etc/hackeros/apps"), apps)
        ]);

        FileSystemMountResolution resolution = router.Resolve(
            VirtualPath.Parse("/etc/hackeros/apps/editor.json"));

        Assert.Same(apps, resolution.Mount.Provider);
        Assert.Equal("/etc/hackeros/apps", resolution.Mount.Path.Value);
    }

    [Fact]
    public void Similar_text_without_segment_boundary_uses_root_provider()
    {
        StubProvider root = new("root");
        StubProvider settings = new("settings");
        FileSystemMountRouter router = new(root,
        [
            new FileSystemMount(VirtualPath.Parse("/etc/hackeros"), settings)
        ]);

        FileSystemMountResolution resolution = router.Resolve(
            VirtualPath.Parse("/etc/hackeros-backup/file.json"));

        Assert.Same(root, resolution.Mount.Provider);
    }

    [Fact]
    public void Root_provider_is_the_deterministic_fallback()
    {
        StubProvider root = new("root");
        FileSystemMountRouter router = new(root);

        FileSystemMountResolution resolution = router.Resolve(VirtualPath.Parse("/home/user/file.txt"));

        Assert.Same(root, resolution.Mount.Provider);
        Assert.Equal("/", resolution.Mount.Path.Value);
        Assert.True(resolution.Mount.IsProtectedRoot);
    }

    [Fact]
    public void Duplicate_mount_paths_are_rejected()
    {
        StubProvider root = new("root");

        Assert.Throws<ArgumentException>(() => new FileSystemMountRouter(root,
        [
            new FileSystemMount(VirtualPath.Parse("/etc/hackeros"), new StubProvider("one")),
            new FileSystemMount(VirtualPath.Parse("/etc/hackeros"), new StubProvider("two"))
        ]));
    }

    [Fact]
    public void Dynamic_mount_is_resolved_then_removed_and_raises_changes()
    {
        StubProvider root = new("root");
        StubProvider drive = new("hostfs:/mnt/projects");
        FileSystemMountRouter router = new(root);
        int changes = 0;
        router.MountsChanged += () => changes++;

        router.Mount(new FileSystemMount(VirtualPath.Parse("/mnt/projects"), drive));

        Assert.Same(drive, router.Resolve(VirtualPath.Parse("/mnt/projects/notes.txt")).Mount.Provider);
        Assert.True(router.Unmount(VirtualPath.Parse("/mnt/projects")));
        Assert.Same(root, router.Resolve(VirtualPath.Parse("/mnt/projects/notes.txt")).Mount.Provider);
        Assert.False(router.Unmount(VirtualPath.Parse("/mnt/projects")));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Dynamic_mount_refuses_taken_nested_and_containing_paths()
    {
        StubProvider root = new("root");
        FileSystemMountRouter router = new(root,
        [
            new FileSystemMount(VirtualPath.Parse("/proc"), new StubProvider("proc"))
        ]);
        router.Mount(new FileSystemMount(VirtualPath.Parse("/mnt/a"), new StubProvider("a")));

        Assert.Throws<InvalidOperationException>(() =>
            router.Mount(new FileSystemMount(VirtualPath.Parse("/mnt/a"), new StubProvider("again"))));
        Assert.Throws<InvalidOperationException>(() =>
            router.Mount(new FileSystemMount(VirtualPath.Parse("/proc/drive"), new StubProvider("inside"))));
        Assert.Throws<InvalidOperationException>(() =>
            router.Mount(new FileSystemMount(VirtualPath.Parse("/mnt"), new StubProvider("around"))));
        Assert.Throws<InvalidOperationException>(() => router.Unmount(VirtualPath.Parse("/proc")));
    }

    private sealed class StubProvider(string providerId) : IFileSystemProvider
    {
        public string ProviderId { get; } = providerId;

        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
            FileSystemReadRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
            FileSystemEnumerateRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> CreateAsync(
            FileSystemCreateRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> WriteAsync(
            FileSystemWriteRequest request,
            IFileSystemContentSource content,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> MoveAsync(
            FileSystemMoveRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> CopyAsync(
            FileSystemCopyRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> DeleteAsync(
            FileSystemDeleteRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
            FileSystemStatRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
            FileSystemSetPermissionsRequest request,
            FileSystemAuthorizationContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}