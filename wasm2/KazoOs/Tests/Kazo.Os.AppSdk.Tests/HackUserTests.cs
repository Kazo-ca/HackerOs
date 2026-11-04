using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>Tests of <see cref="HackUser"/> and <see cref="HackEnvironment.GetFolderPath(HackSpecialFolder)"/> (ADR 0056, section 1).</summary>
public sealed class HackUserTests
{
    [Fact]
    public void Current_describes_the_session_user()
    {
        using IDisposable scope = HackExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: false));

        HackUserInfo user = HackUser.Current;

        Assert.Equal(new HackUserInfo("benoit", "Benoit L.", "/home/benoit", false), user);
    }

    [Fact]
    public void An_administrator_is_flagged()
    {
        Assert.True(HackUser.Describe(new UserContext(AppAuthority.Administrator, isRoot: false)).IsAdministrator);
    }

    [Fact]
    public void Under_sudo_the_effective_user_is_root()
    {
        using IDisposable scope = HackExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: true));

        Assert.Equal(HackUser.Root, HackUser.Current);
        Assert.Equal("/root/Documents", HackEnvironment.GetFolderPath(HackSpecialFolder.MyDocuments));
    }

    [Theory]
    [InlineData(HackSpecialFolder.UserProfile, "/home/benoit")]
    [InlineData(HackSpecialFolder.Desktop, "/home/benoit/Desktop")]
    [InlineData(HackSpecialFolder.MyDocuments, "/home/benoit/Documents")]
    [InlineData(HackSpecialFolder.ApplicationData, "/home/benoit/.config")]
    [InlineData(HackSpecialFolder.LocalApplicationData, "/home/benoit/.local/share")]
    public void GetFolderPath_maps_to_the_linux_layout(HackSpecialFolder folder, string expected)
    {
        using IDisposable scope = HackExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: false));

        Assert.Equal(expected, HackEnvironment.GetFolderPath(folder));
    }

    [Fact]
    public void Outside_any_scope_the_facades_throw()
    {
        Assert.Throws<HackExecutionContextMissingException>(() => HackUser.Current);
        Assert.Throws<HackExecutionContextMissingException>(() => HackEnvironment.GetFolderPath(HackSpecialFolder.Desktop));
    }

    [Fact]
    public async Task A_context_without_catalog_services_reports_them_unsupported()
    {
        using IDisposable scope = HackExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: false));

        await Assert.ThrowsAsync<NotSupportedException>(() => HackApps.GetInstalledAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => HackAppManagement.GetAllAsync());
    }

    private sealed class UserContext(AppAuthority authority, bool isRoot) : IAppExecutionContext
    {
        public AppManifest Manifest => throw new NotSupportedException();

        public Guid InstanceId { get; } = Guid.NewGuid();

        public string UserId => "u-1";

        public string HomeDirectory => "/home/benoit";

        public bool IsRootElevated => isRoot;

        public string UserLoginName => "benoit";

        public string UserDisplayName => "Benoit L.";

        public AppAuthority UserAuthority => authority;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

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
    }
}
