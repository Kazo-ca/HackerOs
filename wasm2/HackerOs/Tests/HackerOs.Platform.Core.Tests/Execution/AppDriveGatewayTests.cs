using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem.HostFolders;
using HackerOs.Platform.Core.Tests.FileSystem.HostFolders;
using HackerOs.Platform.Core.Tests.FileSystem.Virtual;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Metrics;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Execution;

/// <summary>ADR 0057 section 7: <see cref="HackDriveInfo"/> over the OS's <see cref="AppDriveGateway"/>.</summary>
public sealed class AppDriveGatewayTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task Only_the_persistent_root_is_a_drive_and_its_size_is_the_hardware_capacity()
    {
        VirtualFileSystemFixture fixture = new();
        AppDriveGateway gateway = new(
            () => fixture.Router, VirtualHardwareProfile.Default, new FixedUsage(new StorageUsage(3 * GiB, null)));

        HackDriveSnapshot drive = Assert.Single(await gateway.GetDrivesAsync());

        Assert.Equal(new HackDriveSnapshot("/", "idbfs", "hda", 8 * GiB, 5 * GiB), drive);
        Assert.Equal(8 * GiB, VirtualHardwareProfile.DefaultStorageCapacityBytes);
    }

    [Fact]
    public async Task A_smaller_browser_quota_lowers_the_size_and_free_space_never_goes_negative()
    {
        VirtualFileSystemFixture fixture = new();
        AppDriveGateway gateway = new(
            () => fixture.Router, VirtualHardwareProfile.Default, new FixedUsage(new StorageUsage(3 * GiB, 2 * GiB)));

        HackDriveSnapshot drive = Assert.Single(await gateway.GetDrivesAsync());

        Assert.Equal(2 * GiB, drive.TotalSize);
        Assert.Equal(0, drive.AvailableFreeSpace);
    }

    [Fact]
    public async Task HackDriveInfo_reads_the_ambient_apps_drives_like_DriveInfo()
    {
        VirtualFileSystemFixture fixture = new();
        AppDriveGateway gateway = new(
            () => fixture.Router,
            new VirtualHardwareProfile("small", 1, 1, 1, 1, storageCapacityBytes: 1000),
            new FixedUsage(new StorageUsage(250, null)));
        using IDisposable scope = HackExecutionContext.Enter(new DriveOnlyContext(gateway));

        HackDriveInfo drive = Assert.Single(await HackDriveInfo.GetDrivesAsync());

        Assert.Equal("/", drive.Name);
        Assert.Equal("idbfs", drive.DriveFormat);
        Assert.Equal("hda", drive.VolumeLabel);
        Assert.Equal(HackDriveType.Fixed, drive.DriveType);
        Assert.True(drive.IsReady);
        Assert.Equal(1000, drive.TotalSize);
        Assert.Equal(750, drive.AvailableFreeSpace);
        Assert.Equal(750, drive.TotalFreeSpace);
        Assert.Equal("/", drive.RootDirectory.FullName);
    }

    [Fact]
    public async Task A_network_drive_is_listed_with_no_size_and_mounted_through_HackDriveInfo()
    {
        VirtualFileSystemFixture fixture = await VirtualFileSystemFixture.CreateSeededAsync();
        FakeHostFolderAccess access = new() { NextPick = new FakeHostFolder("Projects") };
        NetworkDriveService drives = new(
            fixture.Router, access, () => NetworkDriveServiceTests.Principal("alice", AppAuthority.User));
        AppDriveGateway gateway = new(
            () => fixture.Router, VirtualHardwareProfile.Default, new FixedUsage(new StorageUsage(0, null)))
        {
            NetworkDrives = () => drives
        };
        using IDisposable scope = HackExecutionContext.Enter(new DriveOnlyContext(gateway));

        Assert.True(await HackDriveInfo.IsHostFolderSupportedAsync());
        HackDriveInfo? mounted = await HackDriveInfo.MountHostFolderAsync();
        HackDriveInfo[] all = await HackDriveInfo.GetDrivesAsync();

        Assert.NotNull(mounted);
        Assert.Equal("/mnt/Projects", mounted.Name);
        Assert.Equal(HackDriveType.Network, mounted.DriveType);
        Assert.Equal("hostfs", mounted.DriveFormat);
        Assert.Equal("Projects", mounted.VolumeLabel);
        Assert.Equal(["/", "/mnt/Projects"], all.Select(static drive => drive.Name));
        await HackDriveInfo.UnmountAsync("/mnt/Projects");
        Assert.Single(await HackDriveInfo.GetDrivesAsync());
        await Assert.ThrowsAsync<IOException>(() => HackDriveInfo.UnmountAsync("/mnt/Projects"));
    }

    [Fact]
    public async Task Without_a_network_drive_service_mounting_is_not_supported()
    {
        VirtualFileSystemFixture fixture = new();
        AppDriveGateway gateway = new(() => fixture.Router, VirtualHardwareProfile.Default);

        Assert.False(await gateway.IsHostFolderSupportedAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.MountHostFolderAsync(null).AsTask());
    }

    private sealed class FixedUsage(StorageUsage usage) : IStorageUsageProvider
    {
        public ValueTask<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(usage);
    }

    /// <summary>An execution context where only <see cref="IAppExecutionContext.Drives"/> is wired.</summary>
    private sealed class DriveOnlyContext(IAppDriveGateway drives) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = new()
        {
            Id = "org.tests.drives",
            Name = "Drives",
            Version = "1.0.0",
            PublisherId = "org.tests",
            Description = "HackDriveInfo test application.",
            Kind = AppKind.Terminal,
            EntryPoint = new AppEntryPointManifest("HackerOs.Platform.Core.Tests", "DrivesApp"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };

        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "alice";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotSupportedException();
        public IAppFileSystemGateway FileSystem => throw new NotSupportedException();
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppLoggingGateway Logging => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes => throw new NotSupportedException();
        public IAppDriveGateway Drives { get; } = drives;
    }
}
