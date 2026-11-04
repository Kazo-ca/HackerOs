using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Tracks the close guard captured from each dynamically rendered window app.
/// Registrations are reference-safe so disposal of an old renderer cannot remove
/// a newer component's guard for the same window.
/// </summary>
public sealed class WindowCloseGuardRegistry
{
    private readonly Dictionary<WindowId, IWindowCloseGuard> _guards = [];

    /// <summary>Registers the currently rendered close guard for one window.</summary>
    public IDisposable Register(WindowId windowId, IWindowCloseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guards[windowId] = guard;
        return new Registration(this, windowId, guard);
    }

    /// <summary>Invokes a window's current guard, or permits close when none exists.</summary>
    public ValueTask<bool> ConfirmCloseAsync(
        WindowId windowId,
        CancellationToken cancellationToken = default) =>
        _guards.TryGetValue(windowId, out IWindowCloseGuard? guard)
            ? guard.ConfirmCloseAsync(cancellationToken)
            : ValueTask.FromResult(true);

    private void Unregister(WindowId windowId, IWindowCloseGuard guard)
    {
        if (_guards.TryGetValue(windowId, out IWindowCloseGuard? current)
            && ReferenceEquals(current, guard))
        {
            _guards.Remove(windowId);
        }
    }

    private sealed class Registration(
        WindowCloseGuardRegistry owner,
        WindowId windowId,
        IWindowCloseGuard guard) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(windowId, guard);
            }
        }
    }
}
