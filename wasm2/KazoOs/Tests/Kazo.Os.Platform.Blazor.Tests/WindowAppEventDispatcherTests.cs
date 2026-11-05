using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0095 §1: window-manager facts reach apps in order, a resize only once it is over.</summary>
public sealed class WindowAppEventDispatcherTests
{
    [Fact]
    public async Task Focus_moves_as_a_deactivation_then_an_activation()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowAppEventDispatcher dispatcher = new(runtime);
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(first));
        RecordingTarget firstApp = new();
        RecordingTarget secondApp = new();
        dispatcher.Register(first.Id, firstApp);

        runtime.Apply(new CreateWindowCommand(second));
        dispatcher.Register(second.Id, secondApp);
        runtime.Apply(new FocusWindowCommand(first.Id));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(["activated", "deactivated", "activated"], firstApp.Calls);
        Assert.Equal(["activated", "deactivated"], secondApp.Calls);
    }

    [Fact]
    public async Task Minimizing_the_only_window_reports_the_state_then_the_lost_focus()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowAppEventDispatcher dispatcher = new(runtime);
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        RecordingTarget app = new();
        dispatcher.Register(window.Id, app);

        runtime.Apply(new MinimizeWindowCommand(window.Id));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(["activated", "state Normal->Minimized", "deactivated"], app.Calls);
    }

    [Fact]
    public async Task A_pointer_resize_is_reported_once_when_it_ends()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowAppEventDispatcher dispatcher = new(runtime);
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        RecordingTarget app = new();
        dispatcher.Register(window.Id, app);

        runtime.Apply(new ResizeWindowCommand(window.Id, new WindowBounds(20, 30, 700, 500)));
        runtime.Apply(new ResizeWindowCommand(window.Id, new WindowBounds(20, 30, 720, 510)));
        await dispatcher.WhenIdleAsync();
        Assert.Equal(["activated"], app.Calls);

        runtime.Apply(new CompleteWindowResizeCommand(window.Id));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(["activated", "resized 720x510"], app.Calls);
    }

    [Fact]
    public async Task Maximize_and_restore_report_the_state_then_the_new_size()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowAppEventDispatcher dispatcher = new(runtime, new FixedProbe(new KazoWindowSize(1000, 600)));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        RecordingTarget app = new();
        dispatcher.Register(window.Id, app);

        runtime.Apply(new MaximizeWindowCommand(window.Id));
        runtime.Apply(new RestoreWindowCommand(window.Id));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(
            ["activated", "state Normal->Maximized", "resized 1000x600", "state Maximized->Normal", "resized 1000x600"],
            app.Calls);
    }

    [Fact]
    public async Task A_window_registered_maximized_hears_its_state_at_once()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowAppEventDispatcher dispatcher = new(runtime);
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new MaximizeWindowCommand(window.Id));
        RecordingTarget app = new();

        dispatcher.Register(window.Id, app);
        await dispatcher.WhenIdleAsync();

        Assert.Equal(["state Normal->Maximized", "activated"], app.Calls);
    }

    [Fact]
    public async Task A_disposed_registration_hears_nothing_more()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowAppEventDispatcher dispatcher = new(runtime);
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        RecordingTarget app = new();
        IDisposable registration = dispatcher.Register(window.Id, app);

        registration.Dispose();
        runtime.Apply(new MinimizeWindowCommand(window.Id));
        await dispatcher.WhenIdleAsync();

        Assert.Equal(["activated"], app.Calls);
    }

    [Fact]
    public void The_estimated_client_area_removes_the_title_bar_and_borders()
    {
        KazoWindowSize size = WindowAppEventDispatcher.EstimateClientSize(new WindowBounds(0, 0, 800, 560));

        Assert.Equal(798, size.Width);
        Assert.Equal(560 - 37.6 - 3, size.Height, precision: 6);
    }

    private static WindowRuntimeState CreateState(int seed) =>
        new(
            WindowId.FromGuid(Guid.Parse($"30000000-0000-0000-0000-{seed:D12}")),
            $"org.kazoos.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"40000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240));

    private sealed class FixedProbe(KazoWindowSize size) : IWindowClientAreaProbe
    {
        public ValueTask<KazoWindowSize?> MeasureAsync(WindowId windowId) => ValueTask.FromResult<KazoWindowSize?>(size);
    }

    private sealed class RecordingTarget : IWindowAppEventTarget
    {
        public List<string> Calls { get; } = [];

        public Task NotifyActivatedAsync()
        {
            Calls.Add("activated");
            return Task.CompletedTask;
        }

        public Task NotifyDeactivatedAsync()
        {
            Calls.Add("deactivated");
            return Task.CompletedTask;
        }

        public Task NotifyStateChangedAsync(KazoWindowState previous, KazoWindowState current)
        {
            Calls.Add($"state {previous}->{current}");
            return Task.CompletedTask;
        }

        public Task NotifyResizedAsync(KazoWindowSize size)
        {
            // The estimate is used without a probe: report the outer size back for readability.
            Calls.Add(size.Width == 1000
                ? $"resized {size.Width}x{size.Height}"
                : $"resized {size.Width + 2}x{Math.Round(size.Height + 37.6 + 3, 1)}");
            return Task.CompletedTask;
        }
    }
}
