using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Display;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>ADR 0095 §1: window events reach a window app's OnWindow* hooks.</summary>
public sealed class WindowAppBaseWindowEventsTests
{
    [Fact]
    public async Task Each_notification_calls_the_synchronous_hook_then_its_asynchronous_variant()
    {
        EventApp app = CreateApp();
        IWindowAppEventTarget target = app;

        await target.NotifyActivatedAsync();
        await target.NotifyStateChangedAsync(KazoWindowState.Normal, KazoWindowState.Maximized);
        await target.NotifyResizedAsync(new KazoWindowSize(1278, 640));
        await target.NotifyDeactivatedAsync();

        Assert.Equal(
            [
                "activated", "activated-async",
                "state Normal->Maximized", "state-async Normal->Maximized",
                "resized 1278x640", "resized-async 1278x640",
                "deactivated", "deactivated-async",
            ],
            app.Calls);
        Assert.False(app.Active);
        Assert.Equal(KazoWindowState.Maximized, app.State);
        Assert.Equal(new KazoWindowSize(1278, 640), app.Size);
    }

    [Fact]
    public async Task Activation_is_reported_once_per_transition()
    {
        EventApp app = CreateApp();
        IWindowAppEventTarget target = app;

        await target.NotifyActivatedAsync();
        await target.NotifyActivatedAsync();
        await target.NotifyDeactivatedAsync();
        await target.NotifyDeactivatedAsync();

        Assert.Equal(["activated", "activated-async", "deactivated", "deactivated-async"], app.Calls);
    }

    [Fact]
    public async Task A_failing_hook_is_logged_and_does_not_fail_the_transition()
    {
        RecordingLogging logging = new();
        EventApp app = CreateApp(logging);
        app.ThrowOnStateChange = true;
        IWindowAppEventTarget target = app;

        await target.NotifyStateChangedAsync(KazoWindowState.Normal, KazoWindowState.Minimized);

        Assert.Equal(KazoWindowState.Minimized, app.State);
        (DiagnosticSeverity severity, string message) = Assert.Single(logging.Entries);
        Assert.Equal(DiagnosticSeverity.Error, severity);
        Assert.Contains("OnWindowStateChanged", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tracked_surface_keeps_the_app_active_and_reports_its_own_resizes()
    {
        EventApp app = CreateApp();
        IWindowAppEventTarget target = app;
        FakeSurface surface = new();
        await target.NotifyActivatedAsync();

        using IDisposable tracking = app.Track(surface);
        await target.NotifyDeactivatedAsync();
        Assert.True(app.Active);

        surface.Raise(hasFocus: false, width: 1024, height: 768);
        Assert.False(app.Active);

        surface.Raise(hasFocus: false, width: 900, height: 700);
        Assert.Contains("resized 900x700 on surface-1", app.Calls);
        Assert.Null(app.Size);
    }

    [Fact]
    public async Task A_surface_that_ends_releases_its_focus()
    {
        EventApp app = CreateApp();
        FakeSurface surface = new();
        using IDisposable tracking = app.Track(surface);
        Assert.True(app.Active);

        surface.State = DisplaySurfaceState.ClosedByUser;
        surface.Raise(hasFocus: true, width: 1024, height: 768);

        Assert.False(app.Active);
        await Task.CompletedTask;
    }

    private static EventApp CreateApp(IAppLoggingGateway? logging = null)
    {
        EventApp app = new();
        app.Bind(new MinimalContext(logging ?? new RecordingLogging()));
        return app;
    }

    private sealed class EventApp : WindowAppBase
    {
        public List<string> Calls { get; } = [];

        public bool ThrowOnStateChange { get; set; }

        public bool Active => IsWindowActive;

        public KazoWindowState State => WindowState;

        public KazoWindowSize? Size => WindowSize;

        public void Bind(IAppExecutionContext context) => AppContext = context;

        public IDisposable Track(IDisplaySurface surface) => TrackDisplaySurface(surface);

        protected override void OnWindowActivated() => Calls.Add("activated");

        protected override Task OnWindowActivatedAsync()
        {
            Calls.Add("activated-async");
            return Task.CompletedTask;
        }

        protected override void OnWindowDeactivated() => Calls.Add("deactivated");

        protected override Task OnWindowDeactivatedAsync()
        {
            Calls.Add("deactivated-async");
            return Task.CompletedTask;
        }

        protected override void OnWindowStateChanged(KazoWindowState previous, KazoWindowState current)
        {
            if (ThrowOnStateChange)
            {
                throw new InvalidOperationException("boom");
            }

            Calls.Add($"state {previous}->{current}");
        }

        protected override Task OnWindowStateChangedAsync(KazoWindowState previous, KazoWindowState current)
        {
            Calls.Add($"state-async {previous}->{current}");
            return Task.CompletedTask;
        }

        protected override void OnWindowResized(KazoWindowSize size) =>
            Calls.Add(size.DisplaySurfaceId is null
                ? $"resized {size.Width}x{size.Height}"
                : $"resized {size.Width}x{size.Height} on {size.DisplaySurfaceId}");

        protected override Task OnWindowResizedAsync(KazoWindowSize size)
        {
            if (size.DisplaySurfaceId is null)
            {
                Calls.Add($"resized-async {size.Width}x{size.Height}");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeSurface : IDisplaySurface
    {
        public string Id => "surface-1";

        public string Title => "Detached";

        public int Width { get; private set; } = 1024;

        public int Height { get; private set; } = 768;

        public bool HasFocus { get; private set; } = true;

        public DisplaySurfaceState State { get; set; } = DisplaySurfaceState.Open;

        public event Action<IDisplaySurface>? Changed;

        public void Raise(bool hasFocus, int width, int height)
        {
            HasFocus = hasFocus;
            Width = width;
            Height = height;
            Changed?.Invoke(this);
        }

        public ValueTask SetTitleAsync(string title) => ValueTask.CompletedTask;

        public ValueTask FocusAsync() => ValueTask.CompletedTask;

        public ValueTask CloseAsync() => ValueTask.CompletedTask;

        public ValueTask MountAsync(ElementReference content) => ValueTask.CompletedTask;

        public ValueTask UnmountAsync(ElementReference content) => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogging : IAppLoggingGateway
    {
        public List<(DiagnosticSeverity Severity, string Message)> Entries { get; } = [];

        public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null) =>
            Entries.Add((severity, message));
    }

    private sealed class MinimalContext(IAppLoggingGateway logging) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = new()
        {
            Id = "org.kazoos.window-events",
            Name = "Window Events",
            Version = "1.0.0",
            PublisherId = "org.kazoos",
            Description = "Window events test app.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "EventApp"),
            SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0"),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
        };

        public event EventHandler<Kazo.Os.AppSdk.Diagnostics.KazoStopRequestedEventArgs>? StopRequested
        {
            add { }
            remove { }
        }

        public Guid InstanceId { get; } = Guid.NewGuid();

        public string UserId => "user";

        public AppAuthority UserAuthority => AppAuthority.User;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());

        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);

        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotSupportedException();

        public IAppFileSystemGateway FileSystem => throw new NotSupportedException();

        public IAppEventGateway Events => throw new NotSupportedException();

        public IAppNotificationGateway Notifications => throw new NotSupportedException();

        public IAppLoggingGateway Logging { get; } = logging;

        public IAppClockGateway Clock => throw new NotSupportedException();

        public IAppProcessGateway Processes => throw new NotSupportedException();

        public IAppPermissionErrorGateway PermissionErrors => throw new NotSupportedException();

        public OneShotElevationGrantSet OneShotElevation { get; } = new();

        public IAppEnvironmentGateway Environment { get; } =
            new Kazo.Os.Tests.Support.FakeAppEnvironmentGateway("/home/user", arguments: ["org.kazoos.window-events"]);
    }
}
