using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Windowing.Core.Tests;

/// <summary>ADR 0234 §1-§2: window styles and switches inspired by WinForms.</summary>
public sealed class WindowFrameTests
{
    private static readonly WindowConstraints Resizable = new(true, 320, 240);
    private static readonly WindowConstraints FixedSize = new(false, 320, 240);

    [Fact]
    public void The_default_frame_shows_every_button_of_a_resizable_window()
    {
        WindowFrame frame = WindowFrame.Default;

        Assert.True(frame.HasTitleBar);
        Assert.True(frame.ShowsIcon);
        Assert.True(frame.UserCanMinimize);
        Assert.True(frame.UserCanMaximize(Resizable));
        Assert.True(frame.UserCanResize(Resizable));
        Assert.False(frame.UserCanMaximize(FixedSize));
        Assert.False(frame.UserCanResize(FixedSize));
        Assert.True(frame.ShowInTaskbar);
    }

    [Fact]
    public void A_tool_frame_only_closes_like_a_FixedToolWindow()
    {
        WindowFrame frame = new(WindowFrameStyle.Tool);

        Assert.True(frame.HasTitleBar);
        Assert.False(frame.ShowsIcon);
        Assert.False(frame.UserCanMinimize);
        Assert.False(frame.UserCanMaximize(Resizable));
        Assert.False(frame.UserCanResize(Resizable));
    }

    [Fact]
    public void A_none_frame_has_no_title_bar_and_no_user_geometry()
    {
        WindowFrame frame = new(WindowFrameStyle.None);

        Assert.False(frame.HasTitleBar);
        Assert.False(frame.UserCanMinimize);
        Assert.False(frame.UserCanMaximize(Resizable));
        Assert.False(frame.UserCanResize(Resizable));
    }

    [Fact]
    public void Hiding_the_taskbar_button_also_removes_minimize()
    {
        // Benoit's answer to question 6 (B): a window with no taskbar button cannot be minimized by the user.
        Assert.False(new WindowFrame(ShowInTaskbar: false).UserCanMinimize);
        Assert.False(new WindowFrame(MinimizeBox: false).UserCanMinimize);
        Assert.False(new WindowFrame(MaximizeBox: false).UserCanMaximize(Resizable));
        Assert.True(new WindowFrame(MaximizeBox: false).UserCanResize(Resizable));
        Assert.False(new WindowFrame(ShowIcon: false).ShowsIcon);
    }

    [Fact]
    public void Dialogs_use_the_tool_frame_without_a_taskbar_button()
    {
        Assert.Equal(WindowFrameStyle.Tool, WindowFrame.Dialog.Style);
        Assert.False(WindowFrame.Dialog.ShowInTaskbar);
    }

    [Fact]
    public void Changing_the_frame_at_run_time_raises_one_event_and_keeps_the_geometry()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        WindowFrame none = new(WindowFrameStyle.None);

        IReadOnlyList<WindowEvent> events = runtime.Apply(new SetWindowFrameCommand(window.Id, none));
        IReadOnlyList<WindowEvent> again = runtime.Apply(new SetWindowFrameCommand(window.Id, none));

        WindowFrameChangedEvent changed = Assert.IsType<WindowFrameChangedEvent>(Assert.Single(events));
        Assert.Equal(WindowFrame.Default, changed.PreviousFrame);
        Assert.Equal(none, changed.Frame);
        Assert.Empty(again);
        WindowRuntimeState current = Assert.Single(runtime.Windows);
        Assert.Equal(none, current.Frame);
        Assert.Equal(window.Bounds, current.Bounds);
    }

    [Fact]
    public void A_maximized_window_that_becomes_fixed_size_is_restored()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new MaximizeWindowCommand(window.Id));

        runtime.Apply(new SetWindowFrameCommand(window.Id, WindowFrame.Default, FixedSize));

        WindowRuntimeState current = Assert.Single(runtime.Windows);
        Assert.Equal(WindowVisualState.Normal, current.VisualState);
        Assert.False(current.Constraints.IsResizable);
        Assert.Equal(window.Bounds, current.Bounds);
    }

    [Fact]
    public void Activating_an_owner_reaches_its_topmost_modal_child()
    {
        // A dialog has no taskbar button: clicking its owner's button must bring the dialog forward.
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState owner = CreateState(1);
        WindowRuntimeState modal = CreateState(2, WindowModality.OwnerModal, owner.Id);
        WindowRuntimeState nested = CreateState(3, WindowModality.OwnerModal, modal.Id);
        WindowRuntimeState other = CreateState(4);
        runtime.Apply(new CreateWindowCommand(owner));
        runtime.Apply(new CreateWindowCommand(modal));
        runtime.Apply(new CreateWindowCommand(nested));
        runtime.Apply(new CreateWindowCommand(other));

        Assert.Equal(nested.Id, runtime.ActivationTarget(owner.Id));
        Assert.Equal(nested.Id, runtime.ActivationTarget(modal.Id));
        Assert.Equal(other.Id, runtime.ActivationTarget(other.Id));
        runtime.Apply(new FocusWindowCommand(runtime.ActivationTarget(owner.Id)));
        Assert.True(runtime.Windows.Single(window => window.Id == nested.Id).IsFocused);
    }

    private static WindowRuntimeState CreateState(
        int seed,
        WindowModality modality = WindowModality.Modeless,
        WindowId? ownerId = null) =>
        new(
            WindowId.FromGuid(Guid.Parse($"30000000-0000-0000-0000-{seed:D12}")),
            $"org.kazoos.frame{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"40000000-0000-0000-0000-{seed:D12}")),
            $"Frame {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 1200, 900),
            modality,
            ownerId);
}
