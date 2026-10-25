using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Windowing.Core.Tests;

/// <summary>Tests for the pure single-full-screen-surface decision logic (<c>MOB-009</c>).</summary>
public sealed class SingleSurfacePresentationPolicyTests
{
    [Fact]
    public void SelectPrimary_prefers_the_focused_non_minimized_window()
    {
        WindowRuntimeState background = CreateState(1, isFocused: false, zOrder: 1);
        WindowRuntimeState focused = CreateState(2, isFocused: true, zOrder: 2);

        WindowId? primary = SingleSurfacePresentationPolicy.SelectPrimary([background, focused]);

        Assert.Equal(focused.Id, primary);
    }

    [Fact]
    public void SelectPrimary_falls_back_to_frontmost_non_minimized_when_nothing_is_focused()
    {
        WindowRuntimeState older = CreateState(1, isFocused: false, zOrder: 1);
        WindowRuntimeState newer = CreateState(2, isFocused: false, zOrder: 5);

        WindowId? primary = SingleSurfacePresentationPolicy.SelectPrimary([older, newer]);

        Assert.Equal(newer.Id, primary);
    }

    [Fact]
    public void SelectPrimary_skips_minimized_windows()
    {
        WindowRuntimeState minimizedFocused = CreateState(1, isFocused: true, zOrder: 2, visualState: WindowVisualState.Minimized);
        WindowRuntimeState visible = CreateState(2, isFocused: false, zOrder: 1);

        WindowId? primary = SingleSurfacePresentationPolicy.SelectPrimary([minimizedFocused, visible]);

        Assert.Equal(visible.Id, primary);
    }

    [Fact]
    public void SelectPrimary_returns_null_when_every_window_is_minimized_or_absent()
    {
        Assert.Null(SingleSurfacePresentationPolicy.SelectPrimary([]));

        WindowRuntimeState minimized = CreateState(1, isFocused: false, zOrder: 1, visualState: WindowVisualState.Minimized);
        Assert.Null(SingleSurfacePresentationPolicy.SelectPrimary([minimized]));
    }

    [Fact]
    public void WindowsToMinimize_returns_every_visible_window_except_the_primary()
    {
        WindowRuntimeState primary = CreateState(1, isFocused: true, zOrder: 2);
        WindowRuntimeState other = CreateState(2, isFocused: false, zOrder: 1);
        WindowRuntimeState alreadyMinimized = CreateState(3, isFocused: false, zOrder: 0, visualState: WindowVisualState.Minimized);

        IReadOnlyList<WindowId> toMinimize = SingleSurfacePresentationPolicy.WindowsToMinimize(
            [primary, other, alreadyMinimized], primary.Id);

        Assert.Equal([other.Id], toMinimize);
    }

    [Fact]
    public void ShouldMaximizePrimary_is_true_until_the_primary_is_maximized()
    {
        WindowRuntimeState normal = CreateState(1, isFocused: true, zOrder: 1);
        Assert.True(SingleSurfacePresentationPolicy.ShouldMaximizePrimary([normal], normal.Id));

        WindowRuntimeState maximized = CreateState(1, isFocused: true, zOrder: 1, visualState: WindowVisualState.Maximized);
        Assert.False(SingleSurfacePresentationPolicy.ShouldMaximizePrimary([maximized], maximized.Id));
    }

    [Fact]
    public void ShouldMaximizePrimary_is_false_when_there_is_no_primary()
    {
        Assert.False(SingleSurfacePresentationPolicy.ShouldMaximizePrimary([], null));
    }

    [Fact]
    public void A_dialog_is_the_primary_above_its_owner_even_before_it_has_the_focus()
    {
        // ADR 0171 §3: a dialog is created without the focus; on a phone it must still be what is shown.
        WindowRuntimeState owner = CreateState(1, isFocused: true, zOrder: 1);
        WindowRuntimeState dialog = CreateDialog(2, owner, zOrder: 2);

        Assert.Equal(dialog.Id, SingleSurfacePresentationPolicy.SelectPrimary([owner, dialog]));
        Assert.Equal([owner.Id, dialog.Id], SingleSurfacePresentationPolicy.SelectSurfaceStack([owner, dialog]).Select(w => w.Id));
    }

    [Fact]
    public void A_focused_dialog_brings_its_owner_under_it()
    {
        WindowRuntimeState other = CreateState(3, isFocused: false, zOrder: 5);
        WindowRuntimeState owner = CreateState(1, isFocused: false, zOrder: 1, visualState: WindowVisualState.Minimized);
        WindowRuntimeState dialog = CreateDialog(2, owner, zOrder: 2, isFocused: true);

        IReadOnlyList<WindowRuntimeState> stack = SingleSurfacePresentationPolicy.SelectSurfaceStack([other, owner, dialog]);

        Assert.Equal([owner.Id, dialog.Id], stack.Select(w => w.Id));
    }

    [Fact]
    public void A_minimized_dialog_comes_back_with_its_owner()
    {
        WindowRuntimeState owner = CreateState(1, isFocused: true, zOrder: 1);
        WindowRuntimeState dialog = CreateDialog(2, owner, zOrder: 2, visualState: WindowVisualState.Minimized);

        WindowId? primary = SingleSurfacePresentationPolicy.SelectPrimary([owner, dialog]);

        Assert.Equal(dialog.Id, primary);
        Assert.Equal([owner.Id, dialog.Id], SingleSurfacePresentationPolicy.WindowsToMaximize([owner, dialog], primary));
    }

    [Fact]
    public void The_owner_of_a_primary_dialog_is_not_minimized()
    {
        WindowRuntimeState owner = CreateState(1, isFocused: true, zOrder: 1);
        WindowRuntimeState dialog = CreateDialog(2, owner, zOrder: 2);
        WindowRuntimeState other = CreateState(3, isFocused: false, zOrder: 0);

        IReadOnlyList<WindowId> toMinimize = SingleSurfacePresentationPolicy.WindowsToMinimize([owner, dialog, other], dialog.Id);

        Assert.Equal([other.Id], toMinimize);
    }

    [Fact]
    public void A_dialog_opened_from_a_dialog_is_on_top_of_the_stack()
    {
        WindowRuntimeState owner = CreateState(1, isFocused: true, zOrder: 1);
        WindowRuntimeState dialog = CreateDialog(2, owner, zOrder: 2);
        WindowRuntimeState nested = CreateDialog(3, dialog, zOrder: 3);

        IReadOnlyList<WindowRuntimeState> stack = SingleSurfacePresentationPolicy.SelectSurfaceStack([owner, dialog, nested]);

        Assert.Equal([owner.Id, dialog.Id, nested.Id], stack.Select(w => w.Id));
    }

    [Fact]
    public void WindowsToMaximize_skips_windows_already_maximized()
    {
        WindowRuntimeState owner = CreateState(1, isFocused: true, zOrder: 1, visualState: WindowVisualState.Maximized);
        WindowRuntimeState dialog = CreateDialog(2, owner, zOrder: 2);

        Assert.Equal([dialog.Id], SingleSurfacePresentationPolicy.WindowsToMaximize([owner, dialog], dialog.Id));
    }

    private static WindowRuntimeState CreateDialog(
        int seed,
        WindowRuntimeState owner,
        int zOrder,
        bool isFocused = false,
        WindowVisualState visualState = WindowVisualState.Normal) =>
        new(
            WindowId.FromGuid(Guid.Parse($"30000000-0000-0000-0000-{seed:D12}")),
            owner.AppId,
            owner.OwnerInstanceId,
            $"Dialog {seed}",
            null,
            new WindowBounds(40, 40, 480, 240),
            null,
            zOrder,
            visualState,
            new WindowConstraints(false, 320, 180),
            WindowModality.OwnerModal,
            owner.Id,
            isFocused);

    private static WindowRuntimeState CreateState(
        int seed,
        bool isFocused,
        int zOrder,
        WindowVisualState visualState = WindowVisualState.Normal) =>
        new(
            WindowId.FromGuid(Guid.Parse($"30000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"40000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(0, 0, 640, 480),
            null,
            zOrder,
            visualState,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null,
            isFocused);
}
