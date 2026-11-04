using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>Tests for <see cref="MobileNavigationCommandsAdapter"/>'s <c>MOB-011</c> Back sequence.</summary>
public sealed class MobileNavigationCommandsAdapterTests
{
    [Fact]
    public void RequestHome_minimizes_the_focused_window_without_closing_it()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        MobileNavigationCommandsAdapter adapter = CreateAdapter(runtime);

        adapter.RequestHome();

        WindowRuntimeState after = Assert.Single(runtime.Windows);
        Assert.Equal(WindowVisualState.Minimized, after.VisualState);
    }

    [Fact]
    public void RequestHome_is_a_no_op_when_nothing_is_visible()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        MobileNavigationCommandsAdapter adapter = CreateAdapter(runtime);

        adapter.RequestHome();

        Assert.Empty(runtime.Windows);
    }

    [Fact]
    public void RequestBack_and_RequestRecent_do_not_throw_when_nothing_is_visible()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        MobileNavigationCommandsAdapter adapter = CreateAdapter(runtime);

        adapter.RequestBack();
        adapter.RequestRecent();
    }

    [Fact]
    public async Task RequestBack_invokes_the_primary_windows_registered_back_handler()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        AppBackHandlerRegistry backHandlers = new();
        TaskCompletionSource<AppBackRequest> invoked = new();
        using IDisposable registration = backHandlers.Register(window.Id, new RecordingHandler(invoked, AppBackResult.Handled));
        MobileNavigationCommandsAdapter adapter = CreateAdapter(runtime, backHandlers);

        adapter.RequestBack();

        AppBackRequest request = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AppBackSource.SystemNavigationBar, request.Source);
    }

    [Fact]
    public void RequestBack_dismisses_an_open_overlay_before_anything_else()
    {
        // No await anywhere in this fake's path completes synchronously, so RequestBack's async
        // void body runs to completion before this call returns -- no wait needed.
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        AppBackHandlerRegistry backHandlers = new();
        TaskCompletionSource<AppBackRequest> invoked = new();
        using IDisposable registration = backHandlers.Register(window.Id, new RecordingHandler(invoked, AppBackResult.Handled));
        bool overlayClosed = false;
        MobileNavigationCommandsAdapter adapter = CreateAdapter(
            runtime,
            backHandlers,
            isOverlayOpen: () => true,
            closeOverlay: () => overlayClosed = true);

        adapter.RequestBack();

        Assert.True(overlayClosed);
        Assert.False(invoked.Task.IsCompleted);
    }

    [Fact]
    public void RequestBack_cancels_a_cancelable_modal_dialog_before_consulting_the_back_handler()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState owner = CreateState(1);
        runtime.Apply(new CreateWindowCommand(owner));
        bool dialogCanceled = false;
        WindowRuntimeState dialog = CreateModalState(2, owner.Id, () =>
        {
            dialogCanceled = true;
            return Task.CompletedTask;
        });
        runtime.Apply(new CreateWindowCommand(dialog));
        AppBackHandlerRegistry backHandlers = new();
        TaskCompletionSource<AppBackRequest> invoked = new();
        using IDisposable registration = backHandlers.Register(owner.Id, new RecordingHandler(invoked, AppBackResult.Handled));
        MobileNavigationCommandsAdapter adapter = CreateAdapter(runtime, backHandlers);

        adapter.RequestBack();

        Assert.True(dialogCanceled);
        Assert.False(invoked.Task.IsCompleted);
    }

    [Fact]
    public void RequestBack_returns_to_the_previous_primary_window_when_the_back_handler_declines()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState first = CreateState(1);
        runtime.Apply(new CreateWindowCommand(first));
        MobileNavigationHistory history = new(runtime);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(second));
        AppBackHandlerRegistry backHandlers = new();
        MobileNavigationCommandsAdapter adapter = CreateAdapter(runtime, backHandlers, history: history);

        adapter.RequestBack();

        WindowRuntimeState firstAfter = runtime.Windows.Single(window => window.Id == first.Id);
        Assert.True(firstAfter.IsFocused);
        Assert.Equal(WindowVisualState.Normal, firstAfter.VisualState);
    }

    [Fact]
    public void RequestBack_closes_the_active_surface_when_there_is_no_previous_state()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        AppBackHandlerRegistry backHandlers = new();
        WindowId? closedId = null;
        MobileNavigationCommandsAdapter adapter = CreateAdapter(
            runtime,
            backHandlers,
            closeWindow: id =>
            {
                closedId = id;
                return Task.CompletedTask;
            });

        adapter.RequestBack();

        Assert.Equal(window.Id, closedId);
    }

    private sealed class RecordingHandler(TaskCompletionSource<AppBackRequest> invoked, AppBackResult result) : IAppBackHandler
    {
        public bool CanNavigateBack => true;

#pragma warning disable CS0067 // Required by IAppBackHandler; unused by this test stub.
        public event Action? CanNavigateBackChanged;
#pragma warning restore CS0067

        public ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
        {
            invoked.SetResult(request);
            return ValueTask.FromResult(result);
        }
    }

    private static MobileNavigationCommandsAdapter CreateAdapter(
        WindowRuntime runtime,
        AppBackHandlerRegistry? backHandlers = null,
        MobileNavigationHistory? history = null,
        Func<WindowId, Task>? closeWindow = null,
        Func<bool>? isOverlayOpen = null,
        Action? closeOverlay = null,
        Action? toggleRecent = null) =>
        new(
            runtime,
            backHandlers ?? new AppBackHandlerRegistry(),
            history ?? new MobileNavigationHistory(runtime),
            closeWindow ?? (_ => Task.CompletedTask),
            isOverlayOpen ?? (() => false),
            closeOverlay ?? (() => { }),
            toggleRecent ?? (() => { }));

    private static WindowRuntimeState CreateState(int seed) =>
        new(
            WindowId.FromGuid(Guid.Parse($"50000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"60000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(0, 0, 320, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null,
            isFocused: true);

    private static WindowRuntimeState CreateModalState(int seed, WindowId ownerId, Func<Task> onRequestClose) =>
        new(
            WindowId.FromGuid(Guid.Parse($"50000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"60000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(0, 0, 320, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            WindowModality.OwnerModal,
            ownerId,
            isFocused: true,
            onRequestClose: onRequestClose);
}
