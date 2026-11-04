using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Terminal;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

public sealed class TerminalFullScreenSessionTests
{
    [Fact]
    public async Task Session_ForwardsFramesKeysAndResizeWithoutCrossingOwnershipBoundary()
    {
        List<(TerminalScreenFrame? Frame, bool Active)> changes = [];
        await using TerminalFullScreenSession session = new(
            (frame, active) =>
            {
                changes.Add((frame, active));
                return Task.CompletedTask;
            });

        await session.EnterAlternateScreenAsync();
        TerminalScreenFrame frame = new(["nano"], new TerminalCursor(2, 0), "Nano editor");
        await session.RenderAsync(frame);
        session.UpdateViewport(100, 30);

        TerminalKeyEvent resize = await session.ReadKeyAsync();
        Assert.Equal(TerminalKey.Resize, resize.Key);
        Assert.True(session.TryPostKey(new TerminalKeyEvent(TerminalKey.Character, "x")));
        TerminalKeyEvent character = await session.ReadKeyAsync();

        Assert.Equal("x", character.Text);
        Assert.Equal(TerminalViewport.Create(100, 30), session.Viewport);
        Assert.Contains(changes, change => change.Active && change.Frame == frame);

        await session.LeaveAlternateScreenAsync();
        Assert.False(session.TryPostKey(new TerminalKeyEvent(TerminalKey.Enter)));
        Assert.Equal((null, false), changes[^1]);
    }

    [Fact]
    public async Task ReadKey_HonorsCancellation_AndLeaveIsIdempotent()
    {
        int leaveCount = 0;
        await using TerminalFullScreenSession session = new(
            (frame, active) =>
            {
                if (!active)
                    leaveCount++;
                return Task.CompletedTask;
            });
        await session.EnterAlternateScreenAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.ReadKeyAsync(cancellation.Token));
        await session.LeaveAlternateScreenAsync();
        await session.LeaveAlternateScreenAsync();

        Assert.Equal(1, leaveCount);
    }
}
