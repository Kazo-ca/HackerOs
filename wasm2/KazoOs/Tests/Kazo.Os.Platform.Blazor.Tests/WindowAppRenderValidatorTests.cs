using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Blazor.Tests;

public sealed class WindowAppRenderValidatorTests
{
    [Fact]
    public void Validate_accepts_matching_concrete_window_component()
    {
        RenderFixture fixture = new();

        WindowAppRenderValidator.Validate(fixture.Window, fixture.Descriptor, fixture.Context);
    }

    [Fact]
    public void Validate_rejects_non_window_component_before_render()
    {
        RenderFixture fixture = new();
        AppDescriptor descriptor = new(fixture.Manifest, typeof(string), typeof(string).Assembly);

        Assert.Throws<InvalidOperationException>(() =>
            WindowAppRenderValidator.Validate(fixture.Window, descriptor, fixture.Context));
    }

    [Fact]
    public void Validate_rejects_mismatched_app_identity()
    {
        RenderFixture fixture = new(windowAppId: "org.hackeros.other");

        Assert.Throws<InvalidOperationException>(() =>
            WindowAppRenderValidator.Validate(fixture.Window, fixture.Descriptor, fixture.Context));
    }

    [Fact]
    public void Validate_rejects_mismatched_instance_identity()
    {
        RenderFixture fixture = new(windowInstanceId: Guid.Parse("30000000-0000-0000-0000-000000000099"));

        Assert.Throws<InvalidOperationException>(() =>
            WindowAppRenderValidator.Validate(fixture.Window, fixture.Descriptor, fixture.Context));
    }

    private sealed class RenderFixture
    {
        private readonly Guid _instanceId = Guid.Parse("30000000-0000-0000-0000-000000000001");

        public RenderFixture(string? windowAppId = null, Guid? windowInstanceId = null)
        {
            Manifest = CreateManifest();
            ProcessId processId = ProcessId.FromInt64(7);
            Context = new TestExecutionContext(Manifest, _instanceId, processId);
            Descriptor = new AppDescriptor(Manifest, typeof(TestWindowApp), typeof(TestWindowApp).Assembly);
            Window = new WindowRuntimeState(
                WindowId.FromGuid(Guid.Parse("10000000-0000-0000-0000-000000000001")),
                windowAppId ?? Manifest.Id,
                WindowOwnerId.FromGuid(windowInstanceId ?? _instanceId),
                Manifest.Name,
                null,
                new WindowBounds(20, 20, 640, 480),
                null,
                1,
                WindowVisualState.Normal,
                new WindowConstraints(true, 320, 240));
        }

        public AppManifest Manifest { get; }

        public TestExecutionContext Context { get; }

        public AppDescriptor Descriptor { get; }

        public WindowRuntimeState Window { get; }

        private static AppManifest CreateManifest() => new()
        {
            Id = "org.hackeros.test-window-renderer",
            Name = "Renderer Test",
            Version = "1.0.0",
            PublisherId = "org.hackeros",
            Description = "Valid Window app renderer test manifest.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest(
                typeof(TestWindowApp).Namespace!,
                nameof(TestWindowApp)),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
        };
    }

    private sealed class TestWindowApp : WindowAppBase;

    private sealed class TestExecutionContext(
        AppManifest manifest,
        Guid instanceId,
        ProcessId processId) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;

        public Guid InstanceId { get; } = instanceId;

        public string UserId => "user";

        public AppAuthority UserAuthority => AppAuthority.User;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.Parse("40000000-0000-0000-0000-000000000001"));

        public ProcessId ProcessId { get; } = processId;

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