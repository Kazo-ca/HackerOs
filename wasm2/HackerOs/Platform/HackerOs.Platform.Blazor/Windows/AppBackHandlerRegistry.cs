using HackerOs.AppSdk.Blazor;
using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// Tracks the <see cref="IAppBackHandler"/> and <see cref="IAppForwardHandler"/> captured from each dynamically
/// rendered window app, per doc/en/wasm/dev/old/mobile-interface-platform-plan.md §8 (<c>MOB-012</c>) and
/// ADR 0220 §3. Registrations are reference-safe so disposal of an old renderer cannot remove a newer
/// component's handler for the same window, mirroring <see cref="WindowCloseGuardRegistry"/>.
/// </summary>
public sealed class AppBackHandlerRegistry
{
    private readonly Dictionary<WindowId, IAppBackHandler> _handlers = [];
    private readonly Dictionary<WindowId, IAppForwardHandler> _forwardHandlers = [];

    /// <summary>Registers the currently rendered back handler for one window.</summary>
    public IDisposable Register(WindowId windowId, IAppBackHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(_handlers, windowId, handler);
    }

    /// <summary>Registers the currently rendered forward handler for one window (ADR 0220 §3).</summary>
    public IDisposable RegisterForward(WindowId windowId, IAppForwardHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(_forwardHandlers, windowId, handler);
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

    /// <summary>
    /// Invokes a window's current forward handler, or reports <see cref="AppBackResultStatus.NotHandled"/>
    /// when the window's app doesn't implement <see cref="IAppForwardHandler"/> (ADR 0220 §3).
    /// </summary>
    public ValueTask<AppBackResult> NavigateForwardAsync(
        WindowId windowId,
        AppForwardRequest request,
        CancellationToken cancellationToken = default) =>
        _forwardHandlers.TryGetValue(windowId, out IAppForwardHandler? handler)
            ? handler.NavigateForwardAsync(request, cancellationToken)
            : ValueTask.FromResult(AppBackResult.NotHandled);

    private static Registration<THandler> Add<THandler>(
        Dictionary<WindowId, THandler> handlers,
        WindowId windowId,
        THandler handler)
        where THandler : class
    {
        handlers[windowId] = handler;
        return new Registration<THandler>(handlers, windowId, handler);
    }

    /// <summary>Removes one registration only while it is still the window's current handler.</summary>
    private sealed class Registration<THandler>(
        Dictionary<WindowId, THandler> handlers,
        WindowId windowId,
        THandler handler) : IDisposable
        where THandler : class
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0
                && handlers.TryGetValue(windowId, out THandler? current)
                && ReferenceEquals(current, handler))
            {
                handlers.Remove(windowId);
            }
        }
    }
}
