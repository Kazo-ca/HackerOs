using HackerOs.AppSdk.Blazor;
using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// Tracks the <see cref="IAppBackHandler"/> captured from each dynamically rendered window app,
/// per doc/en/wasm/dev/old/mobile-interface-platform-plan.md §8 (<c>MOB-012</c>). Registrations are
/// reference-safe so disposal of an old renderer cannot remove a newer component's handler for
/// the same window, mirroring <see cref="WindowCloseGuardRegistry"/>.
/// </summary>
public sealed class AppBackHandlerRegistry
{
    private readonly Dictionary<WindowId, IAppBackHandler> _handlers = [];

    /// <summary>Registers the currently rendered back handler for one window.</summary>
    public IDisposable Register(WindowId windowId, IAppBackHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[windowId] = handler;
        return new Registration(this, windowId, handler);
    }

    /// <summary>
    /// Invokes a window's current back handler, or reports <see cref="AppBackResultStatus.NotHandled"/>
    /// when the window's app declares no handler (e.g. it doesn't implement <see cref="IAppBackHandler"/>).
    /// </summary>
    public ValueTask<AppBackResult> NavigateBackAsync(
        WindowId windowId,
        AppBackRequest request,
        CancellationToken cancellationToken = default) =>
        _handlers.TryGetValue(windowId, out IAppBackHandler? handler)
            ? handler.NavigateBackAsync(request, cancellationToken)
            : ValueTask.FromResult(AppBackResult.NotHandled);

    private void Unregister(WindowId windowId, IAppBackHandler handler)
    {
        if (_handlers.TryGetValue(windowId, out IAppBackHandler? current)
            && ReferenceEquals(current, handler))
        {
            _handlers.Remove(windowId);
        }
    }

    private sealed class Registration(
        AppBackHandlerRegistry owner,
        WindowId windowId,
        IAppBackHandler handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(windowId, handler);
            }
        }
    }
}
