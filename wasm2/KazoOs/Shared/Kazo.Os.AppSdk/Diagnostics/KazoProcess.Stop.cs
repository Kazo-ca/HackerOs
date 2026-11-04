using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>Graceful stop and voluntary checkpoints of <see cref="KazoProcess"/> (ADR 0069).</summary>
public sealed partial class KazoProcess
{
    /// <summary>Grace period of <see cref="TerminateAsync"/> when none is given, like <c>kill</c> and "End task".</summary>
    public static readonly TimeSpan DefaultTerminateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the process running the current code: its PID, stop token and
    /// <see cref="KazoCurrentProcess.StopRequested"/> notice.
    /// </summary>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static KazoCurrentProcess Current => new(KazoExecutionContext.Current);

    /// <summary>
    /// A voluntary checkpoint for long work (ADR 0069, section 2): throws when the process is asked
    /// to stop, gives the thread back when the current time slice exceeds 8 ms (one frame at 60 fps),
    /// and otherwise returns at once without allocating.
    /// </summary>
    /// <param name="cancellationToken">The caller's own token, checked with the process's stop token.</param>
    /// <returns>A completed task, or one that completes after the thread was given back.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> or the process's stop token is cancelled.
    /// </exception>
    /// <example>
    /// <code>
    /// foreach (string path in paths)
    /// {
    ///     await KazoProcess.CheckpointAsync(cancellationToken);
    ///     total += (await KazoFile.ReadAllBytesAsync(path, cancellationToken)).Length;
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// Nothing forces a process to call it: it is not an imposed quantum, it is good citizenship. On
    /// the main thread, giving the thread back lets the UI paint and the OS deliver its messages (a
    /// stop request included); in a Web Worker (ADR 0070), it is the only moment the worker can
    /// receive "stop pid 42".
    /// </para>
    /// <para>
    /// Outside any app context (a unit test, a helper thread), only <paramref name="cancellationToken"/>
    /// and the slice are checked.
    /// </para>
    /// </remarks>
    public static ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        IAppExecutionContext? context = KazoExecutionContext.CurrentOrNull;
        ThrowIfStopRequested(context, cancellationToken);
        long processId = context?.ProcessId.Value ?? 0;
        return KazoProcessSlice.IsExhausted(processId)
            ? YieldAsync(processId, cancellationToken)
            : default;
    }

    /// <summary>
    /// Asks the process to stop, like <c>kill</c> (<c>SIGTERM</c>, ADR 0069): it receives
    /// <see cref="KazoCurrentProcess.StopRequested"/> with <see cref="ProcessExitReason.Terminated"/>,
    /// then its stop token is cancelled, and the OS kills it once <paramref name="timeout"/> has
    /// elapsed on the simulated clock.
    /// </summary>
    /// <param name="timeout">Grace period; <see cref="DefaultTerminateTimeout"/> (5 s) when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the wait (the process is still stopped).</param>
    /// <returns><see langword="true"/> when the process ended by itself, <see langword="false"/> when it had to be killed.</returns>
    /// <exception cref="Kazo.Os.AppSdk.IO.KazoUnauthorizedAccessException">It is another process and this app lacks <c>process.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The process has already exited.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public Task<bool> TerminateAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        StopAsync(timeout ?? DefaultTerminateTimeout, ProcessExitReason.Terminated, cancellationToken);

    /// <summary>Shared <c>SIGTERM</c> path of <see cref="TerminateAsync"/> and <see cref="CloseMainWindowAsync"/>.</summary>
    private async Task<bool> StopAsync(TimeSpan timeout, ProcessExitReason reason, CancellationToken cancellationToken)
    {
        IAppProcessGateway gateway = KazoExecutionContext.Current.Processes;
        ProcessRecord stopped;
        try
        {
            // The gateway checks the capability inside its task: a denial surfaces on await.
            stopped = await gateway.StopAsync(ProcessId.FromInt64(Id), timeout, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw Exited(exception);
        }

        HasExited = true;
        return stopped.ExitReason is not (ProcessExitReason.Timeout or ProcessExitReason.Killed);
    }

    private static void ThrowIfStopRequested(IAppExecutionContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context is not null)
        {
            CancellationToken stopToken = context.CancellationToken;
            if (stopToken.IsCancellationRequested || context.StopRequest is not null)
            {
                throw new OperationCanceledException("The process was asked to stop.", stopToken);
            }
        }
    }

    /// <summary>The slow path of <see cref="CheckpointAsync"/>: gives the thread back, then starts a new slice.</summary>
    private static async ValueTask YieldAsync(long processId, CancellationToken cancellationToken)
    {
        await Task.Yield();
        KazoProcessSlice.Begin(processId);
        // The OS may have asked to stop while the thread was away.
        ThrowIfStopRequested(KazoExecutionContext.CurrentOrNull, cancellationToken);
    }
}
