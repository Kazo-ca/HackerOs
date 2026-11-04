using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>Tests of <see cref="KazoUser"/> and <see cref="KazoEnvironment.GetFolderPath(KazoSpecialFolder)"/> (ADR 0056, section 1).</summary>
public sealed class KazoUserTests
{
    [Fact]
    public void Current_describes_the_session_user()
    {
        using IDisposable scope = KazoExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: false));

        KazoUserInfo user = KazoUser.Current;

        Assert.Equal(new KazoUserInfo("benoit", "Benoit L.", "/home/benoit", false), user);
    }

    [Fact]
    public void An_administrator_is_flagged()
    {
        Assert.True(KazoUser.Describe(new UserContext(AppAuthority.Administrator, isRoot: false)).IsAdministrator);
    }

    [Fact]
    public void Under_sudo_the_effective_user_is_root()
    {
        using IDisposable scope = KazoExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: true));

        Assert.Equal(KazoUser.Root, KazoUser.Current);
        Assert.Equal("/root/Documents", KazoEnvironment.GetFolderPath(KazoSpecialFolder.MyDocuments));
    }

    [Theory]
    [InlineData(KazoSpecialFolder.UserProfile, "/home/benoit")]
    [InlineData(KazoSpecialFolder.Desktop, "/home/benoit/Desktop")]
    [InlineData(KazoSpecialFolder.MyDocuments, "/home/benoit/Documents")]
    [InlineData(KazoSpecialFolder.ApplicationData, "/home/benoit/.config")]
    [InlineData(KazoSpecialFolder.LocalApplicationData, "/home/benoit/.local/share")]
    public void GetFolderPath_maps_to_the_linux_layout(KazoSpecialFolder folder, string expected)
    {
        using IDisposable scope = KazoExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: false));

        Assert.Equal(expected, KazoEnvironment.GetFolderPath(folder));
    }

    [Fact]
    public void Outside_any_scope_the_facades_throw()
    {
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoUser.Current);
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoEnvironment.GetFolderPath(KazoSpecialFolder.Desktop));
    }

    [Fact]
    public async Task A_context_without_catalog_services_reports_them_unsupported()
    {
        using IDisposable scope = KazoExecutionContext.Enter(new UserContext(AppAuthority.User, isRoot: false));

        await Assert.ThrowsAsync<NotSupportedException>(() => KazoApps.GetInstalledAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => KazoAppManagement.GetAllAsync());
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
