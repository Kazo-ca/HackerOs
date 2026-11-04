using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>Tests for the <c>MOB-011</c> shell-level navigation history.</summary>
public sealed class MobileNavigationHistoryTests
{
    [Fact]
    public void TryPopPrevious_returns_false_when_only_one_window_has_ever_been_primary()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        runtime.Apply(new CreateWindowCommand(CreateState(1)));
        MobileNavigationHistory history = new(runtime);

        bool popped = history.TryPopPrevious(out _);

        Assert.False(popped);
    }

    [Fact]
    public void TryPopPrevious_returns_the_window_displaced_by_a_newly_created_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState first = CreateState(1);
        runtime.Apply(new CreateWindowCommand(first));
        MobileNavigationHistory history = new(runtime);
        runtime.Apply(new CreateWindowCommand(CreateState(2)));

        bool popped = history.TryPopPrevious(out WindowId previous);

        Assert.True(popped);
        Assert.Equal(first.Id, previous);
    }

    [Fact]
    public void TryPopPrevious_unwinds_multiple_displacements_in_order()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState first = CreateState(1);
        runtime.Apply(new CreateWindowCommand(first));
        MobileNavigationHistory history = new(runtime);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(second));
        runtime.Apply(new CreateWindowCommand(CreateState(3)));

        // Pop back to `second` first (LIFO: the most recently displaced window returns first),
        // simulating the adapter's own restore-then-focus so the return isn't re-pushed.
        bool poppedFirst = history.TryPopPrevious(out WindowId popped1);
        runtime.Apply(new FocusWindowCommand(popped1));

        // From `second`, the next Back should reach all the way back to `first`.
        bool poppedSecond = history.TryPopPrevious(out WindowId popped2);
        runtime.Apply(new FocusWindowCommand(popped2));

        Assert.True(poppedFirst);
        Assert.Equal(second.Id, popped1);
        Assert.True(poppedSecond);
        Assert.Equal(first.Id, popped2);
    }

    [Fact]
    public void TryPopPrevious_discards_stale_entries_for_windows_since_closed()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState first = CreateState(1);
        runtime.Apply(new CreateWindowCommand(first));
        MobileNavigationHistory history = new(runtime);
        runtime.Apply(new CreateWindowCommand(CreateState(2)));
        // first is now on the history stack, but gets closed before Back is ever pressed.
        runtime.Apply(new RequestWindowCloseCommand(first.Id));
        runtime.Apply(new ForceWindowCloseCommand(first.Id));

        bool popped = history.TryPopPrevious(out _);

        Assert.False(popped);
    }

    [Fact]
    public void Restoring_the_popped_window_is_not_re_pushed_as_a_new_forward_navigation()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 375, 667));
        WindowRuntimeState first = CreateState(1);
        runtime.Apply(new CreateWindowCommand(first));
        MobileNavigationHistory history = new(runtime);
        runtime.Apply(new CreateWindowCommand(CreateState(2)));

        history.TryPopPrevious(out WindowId previous);
        runtime.Apply(new FocusWindowCommand(previous));

        // Having returned to `first`, there is nothing left to pop back to from here.
        Assert.False(history.TryPopPrevious(out _));
    }

    private static WindowRuntimeState CreateState(int seed) =>
        new(
            WindowId.FromGuid(Guid.Parse($"70000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"80000000-0000-0000-0000-{seed:D12}")),
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
}
