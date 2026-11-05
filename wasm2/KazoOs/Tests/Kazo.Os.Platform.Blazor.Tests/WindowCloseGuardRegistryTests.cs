using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests;

public sealed class WindowCloseGuardRegistryTests
{
    [Fact]
    public async Task Missing_guard_permits_close_and_registered_guard_controls_result()
    {
        WindowCloseGuardRegistry registry = new();
        WindowId id = WindowId.FromGuid(Guid.NewGuid());

        Assert.True(await registry.ConfirmCloseAsync(id));
        TestGuard guard = new(false);
        using IDisposable registration = registry.Register(id, guard);

        Assert.False(await registry.ConfirmCloseAsync(id));
        Assert.Equal(1, guard.CallCount);
    }

    [Fact]
    public async Task Disposing_stale_registration_does_not_remove_replacement_guard()
    {
        WindowCloseGuardRegistry registry = new();
        WindowId id = WindowId.FromGuid(Guid.NewGuid());
        IDisposable stale = registry.Register(id, new TestGuard(false));
        TestGuard replacement = new(true);
        using IDisposable current = registry.Register(id, replacement);

        stale.Dispose();

        Assert.True(await registry.ConfirmCloseAsync(id));
        Assert.Equal(1, replacement.CallCount);
        current.Dispose();
        Assert.True(await registry.ConfirmCloseAsync(id));
    }

    [Fact]
    public async Task Guard_receives_close_cancellation()
    {
        WindowCloseGuardRegistry registry = new();
        WindowId id = WindowId.FromGuid(Guid.NewGuid());
        using CancellationTokenSource source = new();
        source.Cancel();
        using IDisposable registration = registry.Register(id, new CancellingGuard());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await registry.ConfirmCloseAsync(id, source.Token));
    }

    private sealed class TestGuard(bool result) : IWindowCloseGuard
    {
        public int CallCount { get; private set; }

        public ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellingGuard : IWindowCloseGuard
    {
        public ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }
    }
}
