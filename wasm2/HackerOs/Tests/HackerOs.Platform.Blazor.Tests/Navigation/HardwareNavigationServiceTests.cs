using HackerOs.AppSdk.Blazor;
using HackerOs.Platform.Blazor.Navigation;
using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Tests.Navigation;

/// <summary>ADR 0220 §2-3: hardware Back/Forward gestures reach the active window's handlers.</summary>
public sealed class HardwareNavigationServiceTests
{
    [Fact]
    public async Task Back_from_the_mouse_reaches_the_focused_windows_back_handler_with_its_source()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: true);
        AppBackHandlerRegistry registry = new();
        NavigationRecorder recorder = new();
        using IDisposable registration = registry.Register(window.Id, recorder);
        HardwareNavigationService service = new(runtime, registry);

        await service.NavigateAsync(new HardwareNavigationRequest(HardwareNavigationDirection.Back, AppBackSource.MouseButton));

        AppBackSource source = Assert.Single(recorder.BackSources);
        Assert.Equal(AppBackSource.MouseButton, source);
        Assert.Empty(recorder.ForwardSources);
    }

    [Fact]
    public async Task Forward_reaches_the_focused_windows_forward_handler()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: true);
        AppBackHandlerRegistry registry = new();
        NavigationRecorder recorder = new();
        using IDisposable back = registry.Register(window.Id, recorder);
        using IDisposable forward = registry.RegisterForward(window.Id, recorder);
        HardwareNavigationService service = new(runtime, registry);

        await service.NavigateAsync(new HardwareNavigationRequest(HardwareNavigationDirection.Forward, AppBackSource.KeyboardShortcut));

        Assert.Equal([AppBackSource.KeyboardShortcut], recorder.ForwardSources);
        Assert.Empty(recorder.BackSources);
    }

    [Fact]
    public async Task Nothing_happens_when_no_window_has_the_focus()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: false);
        AppBackHandlerRegistry registry = new();
        NavigationRecorder recorder = new();
        using IDisposable registration = registry.Register(window.Id, recorder);
        HardwareNavigationService service = new(runtime, registry);

        AppBackResult result = await service.NavigateFocusedWindowAsync(
            new HardwareNavigationRequest(HardwareNavigationDirection.Back, AppBackSource.SystemBack));

        Assert.Equal(AppBackResultStatus.NotHandled, result.Status);
        Assert.Empty(recorder.BackSources);
    }

    [Fact]
    public async Task A_window_without_a_forward_handler_reports_not_handled()
    {
        (WindowRuntime runtime, _) = CreateRuntimeWithWindow(focused: true);
        HardwareNavigationService service = new(runtime, new AppBackHandlerRegistry());

        AppBackResult result = await service.NavigateFocusedWindowAsync(
            new HardwareNavigationRequest(HardwareNavigationDirection.Forward, AppBackSource.MouseButton));

        Assert.Equal(AppBackResultStatus.NotHandled, result.Status);
    }

    [Fact]
    public async Task A_throwing_handler_is_reported_as_faulted_instead_of_breaking_the_shell()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: true);
        AppBackHandlerRegistry registry = new();
        using IDisposable registration = registry.Register(window.Id, new NavigationRecorder { Throws = true });
        HardwareNavigationService service = new(runtime, registry);

        AppBackResult result = await service.NavigateFocusedWindowAsync(
            new HardwareNavigationRequest(HardwareNavigationDirection.Back, AppBackSource.MouseButton));

        Assert.Equal(AppBackResultStatus.Faulted, result.Status);
    }

    [Fact]
    public async Task The_shells_route_replaces_the_desktop_default()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: true);
        AppBackHandlerRegistry registry = new();
        NavigationRecorder recorder = new();
        using IDisposable registration = registry.Register(window.Id, recorder);
        HardwareNavigationService service = new(runtime, registry);
        List<HardwareNavigationRequest> routed = [];
        service.Route = request =>
        {
            routed.Add(request);
            return Task.CompletedTask;
        };

        var request = new HardwareNavigationRequest(HardwareNavigationDirection.Back, AppBackSource.SystemBack);
        await service.NavigateAsync(request);

        Assert.Equal([request], routed);
        Assert.Empty(recorder.BackSources);
    }

    [Fact]
    public async Task Mobile_back_passes_the_gesture_source_to_the_app()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: true);
        AppBackHandlerRegistry registry = new();
        NavigationRecorder recorder = new();
        using IDisposable registration = registry.Register(window.Id, recorder);
        MobileNavigationCommandsAdapter adapter = CreateMobileAdapter(runtime, registry);

        await adapter.RequestBackAsync(AppBackSource.SystemBack);

        Assert.Equal([AppBackSource.SystemBack], recorder.BackSources);
    }

    [Fact]
    public async Task Mobile_forward_reaches_the_foreground_app_and_does_nothing_on_home()
    {
        (WindowRuntime runtime, WindowRuntimeState window) = CreateRuntimeWithWindow(focused: true);
        AppBackHandlerRegistry registry = new();
        NavigationRecorder recorder = new();
        using IDisposable registration = registry.RegisterForward(window.Id, recorder);
        MobileNavigationCommandsAdapter adapter = CreateMobileAdapter(runtime, registry);

        await adapter.RequestForwardAsync(AppBackSource.MouseButton);
        runtime.Apply(new MinimizeWindowCommand(window.Id));
        await adapter.RequestForwardAsync(AppBackSource.MouseButton);

        Assert.Equal([AppBackSource.MouseButton], recorder.ForwardSources);
    }

    private static MobileNavigationCommandsAdapter CreateMobileAdapter(WindowRuntime runtime, AppBackHandlerRegistry registry) =>
        new(runtime, registry, new MobileNavigationHistory(runtime), _ => Task.CompletedTask, () => false, () => { }, () => { });

    private static (WindowRuntime Runtime, WindowRuntimeState Window) CreateRuntimeWithWindow(bool focused)
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = new(
            WindowId.FromGuid(Guid.Parse("70000000-0000-0000-0000-000000000001")),
            "org.hackeros.test",
            WindowOwnerId.FromGuid(Guid.Parse("80000000-0000-0000-0000-000000000001")),
            "Test",
            null,
            new WindowBounds(0, 0, 320, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null,
            isFocused: focused);
        runtime.Apply(new CreateWindowCommand(window));
        if (!focused)
        {
            // Creating a window may give it the focus: take it away explicitly.
            runtime.Apply(new MinimizeWindowCommand(window.Id));
        }

        return (runtime, runtime.Windows.Single());
    }

    private sealed class NavigationRecorder : IAppBackHandler, IAppForwardHandler
    {
        public List<AppBackSource> BackSources { get; } = [];

        public List<AppBackSource> ForwardSources { get; } = [];

        public bool Throws { get; init; }

        public bool CanNavigateBack => true;

        public bool CanNavigateForward => true;

#pragma warning disable CS0067 // Required by the interfaces; unused by this stub.
        public event Action? CanNavigateBackChanged;

        public event Action? CanNavigateForwardChanged;
#pragma warning restore CS0067

        public ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("broken handler");
            }

            BackSources.Add(request.Source);
            return ValueTask.FromResult(AppBackResult.Handled);
        }

        public ValueTask<AppBackResult> NavigateForwardAsync(AppForwardRequest request, CancellationToken cancellationToken = default)
        {
            ForwardSources.Add(request.Source);
            return ValueTask.FromResult(AppBackResult.Handled);
        }
    }
}
