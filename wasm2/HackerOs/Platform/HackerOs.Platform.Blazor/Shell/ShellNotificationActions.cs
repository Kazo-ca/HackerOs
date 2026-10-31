namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The notification actions the shell itself runs (ADR 0197 §4): a host registers what one of its
/// action ids does, such as « Redémarrer » of the « update ready » notification, and the clock panel
/// and the notification bubbles run it when the user chooses that action. An action registered
/// nowhere and made by no <see cref="HackerOs.Simulation.Abstractions.Notifications.NotificationAction.LaunchApp(string, string)"/>
/// belongs to its app.
/// </summary>
public sealed class ShellNotificationActions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Func<Task>> _handlers = new(StringComparer.Ordinal);

    /// <summary>Registers what an action id does, until the returned handle is disposed.</summary>
    /// <param name="actionId">The action id, as in <see cref="HackerOs.Simulation.Abstractions.Notifications.NotificationAction.Id"/>.</param>
    /// <param name="handler">Runs the action; the notification is dismissed before it runs.</param>
    /// <returns>A handle that removes this registration (and only this one).</returns>
    public IDisposable Register(string actionId, Func<Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers[actionId] = handler;
        }

        return new Registration(this, actionId, handler);
    }

    /// <summary>Gets what an action id does, when a host registered it.</summary>
    /// <param name="actionId">The action id.</param>
    /// <param name="handler">The handler, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the shell runs this action.</returns>
    public bool TryGet(string actionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Func<Task>? handler)
    {
        lock (_gate)
        {
            return _handlers.TryGetValue(actionId, out handler);
        }
    }

    private void Remove(string actionId, Func<Task> handler)
    {
        lock (_gate)
        {
            // A later registration of the same id replaced this one: keep it.
            if (_handlers.TryGetValue(actionId, out Func<Task>? current) && ReferenceEquals(current, handler))
            {
                _handlers.Remove(actionId);
            }
        }
    }

    private sealed class Registration(ShellNotificationActions owner, string actionId, Func<Task> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(actionId, handler);
            }
        }
    }
}
