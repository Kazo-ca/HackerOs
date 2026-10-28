using HackerOs.App.Abstractions;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>Verifies successful app processes are represented by desktop and taskbar windows.</summary>
public sealed class WindowLaunchCoordinatorTests
{
    [Theory]
    [InlineData("icons/app.svg", "_content/Test/icons/app.svg")]
    [InlineData("icons/my icon.svg", "_content/Test/icons/my%20icon.svg")]
    public async Task A_declared_icon_is_resolved_under_the_app_static_assets(string path, string expected)
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest() with
        {
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, [path, "icons/large.svg"]),
        };

        await coordinator.PresentAsync(manifest, CreateProcess());

        Assert.Equal(expected, Assert.Single(runtime.Windows).IconAssetPath);
    }

    [Fact]
    public async Task Present_creates_window_for_successful_window_launch()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        ProcessRecord process = CreateProcess();

        bool presented = await coordinator.PresentAsync(
            manifest,
            new AppLaunchResult(AppLaunchStatus.Launched, process));

        Assert.True(presented);
        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(manifest.Id, window.AppId);
        Assert.Equal(WindowOwnerId.FromGuid(process.AppInstanceId.Value), window.OwnerInstanceId);
        Assert.True(window.IsFocused);
    }

    [Fact]
    public async Task A_reserved_shell_surface_gets_no_window_until_the_reservation_ends()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();

        IDisposable first = coordinator.ReserveShellSurface(manifest.Id);
        IDisposable second = coordinator.ReserveShellSurface(manifest.Id);
        Assert.False(await coordinator.PresentAsync(manifest, CreateProcess()));
        Assert.Empty(runtime.Windows);

        first.Dispose();
        first.Dispose();
        Assert.True(coordinator.IsShellSurface(manifest.Id));
        second.Dispose();
        Assert.False(coordinator.IsShellSurface(manifest.Id));
        Assert.True(await coordinator.PresentAsync(manifest, CreateProcess()));
    }

    [Fact]
    public async Task Present_restores_existing_singleton_window_without_duplicating_it()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        ProcessRecord process = CreateProcess();
        AppLaunchResult launch = new(AppLaunchStatus.Launched, process);
        await coordinator.PresentAsync(manifest, launch);
        WindowId windowId = Assert.Single(runtime.Windows).Id;
        runtime.Apply(new MinimizeWindowCommand(windowId));

        bool presented = await coordinator.PresentAsync(
            manifest,
            new AppLaunchResult(AppLaunchStatus.FocusedExisting, process));

        Assert.True(presented);
        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(WindowVisualState.Normal, window.VisualState);
        Assert.True(window.IsFocused);
    }

    [Fact]
    public async Task Present_process_overload_creates_window_for_window_app()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        ProcessRecord process = CreateProcess();

        bool presented = await coordinator.PresentAsync(manifest, process);

        Assert.True(presented);
        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(manifest.Id, window.AppId);
        Assert.Equal(WindowOwnerId.FromGuid(process.AppInstanceId.Value), window.OwnerInstanceId);
    }

    [Fact]
    public async Task Present_process_overload_focuses_existing_window_instead_of_duplicating()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        ProcessRecord process = CreateProcess();
        await coordinator.PresentAsync(manifest, process);
        WindowId windowId = Assert.Single(runtime.Windows).Id;
        runtime.Apply(new MinimizeWindowCommand(windowId));

        bool presented = await coordinator.PresentAsync(manifest, process);

        Assert.True(presented);
        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(WindowVisualState.Normal, window.VisualState);
        Assert.True(window.IsFocused);
    }

    [Fact]
    public async Task Present_projects_the_manifests_supportsBack_declaration_onto_the_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest() with { SupportsBack = true };
        ProcessRecord process = CreateProcess();

        await coordinator.PresentAsync(manifest, process);

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.True(window.SupportsBack);
    }

    [Fact]
    public async Task Present_defaults_supportsBack_to_false_when_the_manifest_does_not_declare_it()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        ProcessRecord process = CreateProcess();

        await coordinator.PresentAsync(manifest, process);

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.False(window.SupportsBack);
    }

    [Fact]
    public async Task Present_process_overload_ignores_non_window_apps()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest() with { Kind = AppKind.Terminal };
        ProcessRecord process = CreateProcess();

        bool presented = await coordinator.PresentAsync(manifest, process);

        Assert.False(presented);
        Assert.Empty(runtime.Windows);
    }

    [Fact]
    public async Task An_app_without_declared_sizes_keeps_the_former_800_by_560_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);

        await coordinator.PresentAsync(CreateManifest(), CreateProcess());

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(800, window.Bounds.Width);
        Assert.Equal(560, window.Bounds.Height);
        Assert.Equal(360, window.Constraints.MinWidth);
        Assert.Equal(240, window.Constraints.MinHeight);
        Assert.Null(window.Constraints.MaxWidth);
        Assert.True(window.Constraints.IsResizable);
    }

    [Fact]
    public async Task Declared_window_sizes_set_the_initial_size_and_the_constraints()
    {
        // ADR 0095 §2.
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest() with
        {
            Window = new WindowOptions
            {
                DefaultWidth = 420,
                DefaultHeight = 600,
                MinWidth = 300,
                MinHeight = 400,
                MaxWidth = 640,
                MaxHeight = 700,
                Resizable = false,
            },
        };

        await coordinator.PresentAsync(manifest, CreateProcess());

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(420, window.Bounds.Width);
        Assert.Equal(600, window.Bounds.Height);
        Assert.Equal(new WindowConstraints(false, 300, 400, 640, 700), window.Constraints);
    }

    [Fact]
    public void The_initial_size_shrinks_to_a_small_screen_but_never_below_the_minimum()
    {
        WindowOptions options = new() { DefaultWidth = 1200, DefaultHeight = 900, MinWidth = 500, MinHeight = 700 };

        WindowBounds bounds = WindowLaunchCoordinator.InitialBounds(options, 0, new WindowBounds(0, 0, 1024, 600));

        Assert.Equal(1024, bounds.Width);
        Assert.Equal(700, bounds.Height);
    }

    [Fact]
    public async Task An_app_started_from_a_screen_start_menu_opens_on_that_screen()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        DisplayId screen2 = DisplayId.FromNumber(2);
        runtime.Apply(new AddDisplayCommand(screen2, new WindowBounds(0, 0, 900, 600)));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();

        using (coordinator.OpenOnDisplay(manifest.Id, screen2))
        {
            await coordinator.PresentAsync(manifest, CreateProcess());
        }

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(screen2, window.DisplayId);
        Assert.True(window.Bounds.Width <= 900);
        Assert.True(window.Bounds.Height <= 600);
    }

    [Fact]
    public async Task A_window_opened_otherwise_opens_on_the_screen_of_the_focused_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        DisplayId screen2 = DisplayId.FromNumber(2);
        runtime.Apply(new AddDisplayCommand(screen2, new WindowBounds(0, 0, 900, 600)));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        using (coordinator.OpenOnDisplay(manifest.Id, screen2))
        {
            await coordinator.PresentAsync(manifest, CreateProcess());
        }

        // The scope ended: a second process (a command, another app) follows the focused window.
        await coordinator.PresentAsync(manifest with { Id = "org.hackeros.other" }, CreateProcess());

        Assert.All(runtime.Windows, window => Assert.Equal(screen2, window.DisplayId));
    }

    [Fact]
    public async Task A_screen_closed_before_the_window_opens_falls_back_to_screen_1()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();

        using (coordinator.OpenOnDisplay(manifest.Id, DisplayId.FromNumber(4)))
        {
            await coordinator.PresentAsync(manifest, CreateProcess());
        }

        Assert.Equal(DisplayId.Primary, Assert.Single(runtime.Windows).DisplayId);
    }

    private static AppManifest CreateManifest() => new()
    {
        Id = "org.hackeros.test-window",
        Name = "Test Window",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Window launch coordinator test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test.dll", "Test.Window"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        SingleInstancePerUser = true
    };

    private static ProcessRecord CreateProcess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProcessRecord(
            ProcessId.FromInt64(42),
            parentPid: null,
            "org.hackeros.test-window",
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Window,
            LocalUserId.FromGuid(Guid.NewGuid()),
            SessionId.FromGuid(Guid.NewGuid()),
            ProcessState.Running,
            ResourceProfile.None,
            serviceHealth: null,
            now,
            startedAtUtc: now,
            stoppedAtUtc: null,
            exitCode: null,
            exitReason: null);
    }
}
