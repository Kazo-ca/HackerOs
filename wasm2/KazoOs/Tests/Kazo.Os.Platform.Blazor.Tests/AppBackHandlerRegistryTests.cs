using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests;

public sealed class AppBackHandlerRegistryTests
{
    [Fact]
    public async Task Missing_handler_reports_not_handled_and_registered_handler_is_invoked()
    {
        AppBackHandlerRegistry registry = new();
        WindowId id = WindowId.FromGuid(Guid.NewGuid());
        AppBackRequest request = new(AppBackSource.DesktopChromeButton);

        AppBackResult missing = await registry.NavigateBackAsync(id, request);
        Assert.Equal(AppBackResultStatus.NotHandled, missing.Status);

        TestHandler handler = new(AppBackResult.Handled);
        using IDisposable registration = registry.Register(id, handler);

        AppBackResult handled = await registry.NavigateBackAsync(id, request);
        Assert.Equal(AppBackResultStatus.Handled, handled.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Disposing_stale_registration_does_not_remove_replacement_handler()
    {
        AppBackHandlerRegistry registry = new();
        WindowId id = WindowId.FromGuid(Guid.NewGuid());
        AppBackRequest request = new(AppBackSource.DesktopChromeButton);
        IDisposable stale = registry.Register(id, new TestHandler(AppBackResult.NotHandled));
        TestHandler replacement = new(AppBackResult.Handled);
        using IDisposable current = registry.Register(id, replacement);

        stale.Dispose();

        AppBackResult result = await registry.NavigateBackAsync(id, request);
        Assert.Equal(AppBackResultStatus.Handled, result.Status);
        Assert.Equal(1, replacement.CallCount);
        current.Dispose();

        AppBackResult afterDispose = await registry.NavigateBackAsync(id, request);
        Assert.Equal(AppBackResultStatus.NotHandled, afterDispose.Status);
    }

    [Fact]
    public async Task Handler_receives_the_request_and_cancellation_token()
    {
        AppBackHandlerRegistry registry = new();
        WindowId id = WindowId.FromGuid(Guid.NewGuid());
        using CancellationTokenSource source = new();
        source.Cancel();
        using IDisposable registration = registry.Register(id, new CancellingHandler());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await registry.NavigateBackAsync(id, new AppBackRequest(AppBackSource.SystemNavigationBar), source.Token));
    }

    private sealed class TestHandler(AppBackResult result) : IAppBackHandler
    {
        public int CallCount { get; private set; }

        public bool CanNavigateBack => true;

#pragma warning disable CS0067 // Required by IAppBackHandler; unused by these test stubs.
        public event Action? CanNavigateBackChanged;
#pragma warning restore CS0067

        public ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellingHandler : IAppBackHandler
    {
        public bool CanNavigateBack => true;

#pragma warning disable CS0067 // Required by IAppBackHandler; unused by these test stubs.
        public event Action? CanNavigateBackChanged;
#pragma warning restore CS0067

        public ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(AppBackResult.Handled);
        }
    }
}
