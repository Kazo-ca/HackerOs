using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

public sealed class DesktopShellTests
{
    [Fact]
    public void Taskbar_window_click_minimizes_focused_window_and_restores_minimized_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));

        Assert.True(runtime.Windows[0].IsFocused);

        // Clicking active window minimizes it
        runtime.Apply(new MinimizeWindowCommand(window.Id));
        Assert.Equal(WindowVisualState.Minimized, runtime.Windows[0].VisualState);

        // Restoring brings window back to Normal and focused
        runtime.Apply(new RestoreWindowCommand(window.Id));
        runtime.Apply(new FocusWindowCommand(window.Id));
        Assert.Equal(WindowVisualState.Normal, runtime.Windows[0].VisualState);
        Assert.True(runtime.Windows[0].IsFocused);
    }

    [Fact]
    public void AppCatalog_provides_validated_manifests_for_launcher()
    {
        AppManifest app = CreateAppManifest("org.hackeros.testapp", "Test App", AppKind.Window);
        AppCatalogBuildResult result = AppCatalog.Build([app]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Catalog);
        Assert.True(result.Catalog.Manifests.ContainsKey("org.hackeros.testapp"));
    }

    [Fact]
    public void NotificationQueue_enqueues_and_dismisses_active_notifications()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        LocalUserId userId = LocalUserId.FromGuid(Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Notification record = new(
            NotificationId.FromGuid(Guid.NewGuid()),
            userId,
            "org.hackeros.testapp",
            NotificationSeverity.Information,
            "Test Notice",
            "Test notification message",
            [],
            now,
            now.AddMinutes(5));

        queue.Enqueue(record);
        Assert.Single(queue.GetActive(userId, now));

        queue.Dismiss(record.Id);
        Assert.Empty(queue.GetActive(userId, now));
    }

    private static WindowRuntimeState CreateState(int seed) =>
        new(
            WindowId.FromGuid(Guid.Parse($"10000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"20000000-0000-0000-0000-{seed:D12}")),
            $"Test App {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 1200, 900),
            WindowModality.Modeless,
            null);

    private static AppManifest CreateAppManifest(string id, string name, AppKind kind) =>
        new()
        {
            Id = id,
            Name = name,
            Version = "1.0.0",
            PublisherId = "pub.hackeros",
            Description = "Test application description",
            Kind = kind,
            EntryPoint = new AppEntryPointManifest("TestAssembly.dll", "TestAssembly.TestComponent"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };
}
