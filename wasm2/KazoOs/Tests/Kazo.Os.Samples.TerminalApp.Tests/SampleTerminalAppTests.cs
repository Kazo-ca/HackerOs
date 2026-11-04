using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Xunit;

namespace Kazo.Os.Samples.TerminalApp.Tests;

public sealed class SampleTerminalAppTests
{
    private static readonly AppManifest Manifest = new()
    {
        SchemaVersion = 1,
        Id = "org.kazoos.samples.terminal-app",
        Name = "Sample Terminal App",
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Sample Terminal command application",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Samples.TerminalApp.dll", "Kazo.Os.Samples.TerminalApp.SampleTerminalApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
        Terminal = new TerminalCommandManifest("sample-cmd", ["sample"], "Executes sample SDK terminal command"),
        Capabilities = [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite],
        Resources = AppResourceProfileManifest.None
    };

    [Fact]
    public void Manifest_HasTerminalKindAndCommandMetadata()
    {
        SampleTerminalApp app = new(Manifest);
        Assert.Equal(AppKind.Terminal, app.Manifest.Kind);
        Assert.NotNull(app.Manifest.Terminal);
        Assert.Equal("sample-cmd", app.Manifest.Terminal.Name);
    }

    [Fact]
    public async Task ExecuteAsync_WritesOutputAndReturnsZero()
    {
        SampleTerminalApp app = new(Manifest);
        StringWriter stdOut = new();
        StringWriter stdErr = new();
        TestExecutionContext appCtx = new(Manifest);
        TerminalExecutionContext termCtx = new(
            appCtx,
            ["Hello SDK 1.0"],
            TextReader.Null,
            stdOut,
            stdErr,
            "/home/user-1",
            new Dictionary<string, string>());

        int exitCode = await app.ExecuteAsync(termCtx, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Hello SDK 1.0", stdOut.ToString());
    }

    private sealed class TestExecutionContext : IAppExecutionContext
    {
        public TestExecutionContext(AppManifest manifest)
        {
            Manifest = manifest;
            Logging = new TestLoggingGateway();
        }

        public AppManifest Manifest { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user-1";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(100);
        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging { get; }
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }

    private sealed class TestLoggingGateway : IAppLoggingGateway
    {
        public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null)
        {
        }
    }
}
