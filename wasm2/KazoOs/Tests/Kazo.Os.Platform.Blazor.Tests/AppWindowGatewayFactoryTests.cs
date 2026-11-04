using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0117 §2-3: each app window gets a gateway that renames that window only.</summary>
public sealed class AppWindowGatewayFactoryTests
{
    [Fact]
    public async Task A_gateway_renames_its_own_window_and_never_another_window_of_the_same_app()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        await coordinator.PresentAsync(manifest, CreateProcess());
        await coordinator.PresentAsync(manifest, CreateProcess());
        WindowRuntimeState[] windows = [.. runtime.Windows];
        AppWindowGatewayFactory factory = new(runtime, coordinator);
        IAppWindowGateway first = factory.Create(windows[0].Id, manifest);

        string shown = await first.SetTitleAsync("photo.png — Hack Paint");

        Assert.Equal("photo.png — Hack Paint", shown);
        Assert.Equal("photo.png — Hack Paint", first.Title);
        Assert.Equal("photo.png — Hack Paint", runtime.Windows.Single(window => window.Id == windows[0].Id).Title);
        Assert.Equal("Test Window", runtime.Windows.Single(window => window.Id == windows[1].Id).Title);
    }

    [Fact]
    public async Task The_title_is_cleaned_and_an_empty_one_restores_the_app_name()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        await coordinator.PresentAsync(manifest, CreateProcess());
        WindowId windowId = Assert.Single(runtime.Windows).Id;
        IAppWindowGateway gateway = new AppWindowGatewayFactory(runtime, coordinator).Create(windowId, manifest);

        Assert.Equal("Test Window", gateway.DefaultTitle);
        Assert.Equal("*a.txt — Test Window", await gateway.SetTitleAsync("\t*a.txt — Test Window\r\n"));
        Assert.Equal("Test Window", await gateway.SetTitleAsync(null));
        Assert.Equal("Test Window", Assert.Single(runtime.Windows).Title);
        Assert.Equal("Test Window", await gateway.SetTitleAsync("   "));
    }

    [Fact]
    public async Task A_closed_window_is_left_alone()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        await coordinator.PresentAsync(manifest, CreateProcess());
        WindowId windowId = Assert.Single(runtime.Windows).Id;
        IAppWindowGateway gateway = new AppWindowGatewayFactory(runtime).Create(windowId, manifest);
        runtime.Apply(new ForceWindowCloseCommand(windowId));

        string shown = await gateway.SetTitleAsync("late.png — Hack Paint");

        Assert.Equal("late.png — Hack Paint", shown);
        Assert.Equal("late.png — Hack Paint", gateway.Title);
        Assert.Empty(runtime.Windows);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_applied()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowLaunchCoordinator coordinator = new(runtime);
        AppManifest manifest = CreateManifest();
        await coordinator.PresentAsync(manifest, CreateProcess());
        WindowId windowId = Assert.Single(runtime.Windows).Id;
        IAppWindowGateway gateway = new AppWindowGatewayFactory(runtime).Create(windowId, manifest);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.SetTitleAsync("ignored", new CancellationToken(canceled: true)).AsTask());

        Assert.Equal("Test Window", Assert.Single(runtime.Windows).Title);
    }

    private static AppManifest CreateManifest() => new()
    {
        Id = "org.hackeros.test-window",
        Name = "Test Window",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Window gateway test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test.dll", "Test.Window"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
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
