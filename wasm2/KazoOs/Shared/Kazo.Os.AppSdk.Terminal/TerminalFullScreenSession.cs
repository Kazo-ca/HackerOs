using System.Threading.Channels;
using Kazo.Os.AppSdk;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// Bridges renderer-independent full-screen commands to one Terminal window instance.
/// Each window owns its channel, viewport, and screen state; no input can leak between tabs
/// or command executions.
/// </summary>
public sealed class TerminalFullScreenSession : IFullScreenTerminalSession, IAsyncDisposable
{
    private readonly Channel<TerminalKeyEvent> _input = Channel.CreateUnbounded<TerminalKeyEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Func<TerminalScreenFrame?, bool, Task> _screenChanged;
    private int _active;
    private int _disposed;

    /// <summary>Creates a session that reports screen changes to its owning renderer.</summary>
    public TerminalFullScreenSession(Func<TerminalScreenFrame?, bool, Task> screenChanged)
    {
        _screenChanged = screenChanged ?? throw new ArgumentNullException(nameof(screenChanged));
    }

    /// <inheritdoc />
    public TerminalViewport Viewport { get; private set; } = TerminalViewport.Create(80, 24);

    /// <summary>Gets whether a command currently owns the alternate screen.</summary>
    public bool IsActive => Volatile.Read(ref _active) == 1;

    /// <inheritdoc />
    public async ValueTask EnterAlternateScreenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("The terminal alternate screen is already active.");

        await _screenChanged(null, true);
    }

    /// <inheritdoc />
    public async ValueTask RenderAsync(
        TerminalScreenFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActive();
        await _screenChanged(frame, true);
    }

    /// <inheritdoc />
    public async ValueTask<TerminalKeyEvent> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return await _input.Reader.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask LeaveAlternateScreenAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            return;

        await _screenChanged(null, false);
    }

    /// <summary>Queues one renderer key event for the active command.</summary>
    public bool TryPostKey(TerminalKeyEvent keyEvent) =>
        IsActive && _input.Writer.TryWrite(keyEvent);

    /// <summary>
    /// Updates character-cell dimensions and queues a resize event when a command owns the screen.
    /// </summary>
    public void UpdateViewport(int columns, int rows)
    {
        TerminalViewport next = TerminalViewport.Create(columns, rows);
        if (next == Viewport)
            return;

        Viewport = next;
        if (IsActive)
            _input.Writer.TryWrite(new TerminalKeyEvent(TerminalKey.Resize));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await LeaveAlternateScreenAsync();
        _input.Writer.TryComplete();
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!IsActive)
            throw new InvalidOperationException("No command owns the terminal alternate screen.");
    }
}
