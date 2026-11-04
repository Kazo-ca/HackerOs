using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Processes;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Runs a headless process somewhere other than the OS's own thread (ADR 0070): in a Web Worker of
/// the pool or in a worker of its own. Implemented by <c>Kazo.Os.Platform.WorkerHost</c> and
/// registered only by a host that has Web Workers (the WebAssembly hosts); without it, the
/// orchestrator runs every process in-process, as before (ADR 0070, section 6).
/// </summary>
/// <remarks>
/// The OS still creates the process, its trusted <see cref="IAppExecutionContext"/> and its control
/// block on its own thread: the worker only runs the app's code, and every SDK call it makes comes
/// back as a message served by that same context, with the same capability checks.
/// </remarks>
public interface IRemoteProcessHost
{
    /// <summary>
    /// Gets whether this app runs in a worker on this host: its manifest asks for
    /// <see cref="AppExecutionHost.Pool"/> or <see cref="AppExecutionHost.Dedicated"/> and its kind
    /// can be hosted remotely (a command or a service).
    /// </summary>
    /// <param name="manifest">The app's manifest.</param>
    bool ShouldRunRemotely(AppManifest manifest);

    /// <summary>
    /// Runs one command in a worker until it exits. The streams, arguments, working directory and
    /// environment are those of <paramref name="launch"/>; the stop request and the kill of the
    /// process reach the worker through <see cref="RemoteProcessLaunch.Control"/>.
    /// </summary>
    /// <param name="launch">The process to run.</param>
    /// <param name="terminal">The command's streams and arguments.</param>
    /// <param name="cancellationToken">Interrupts the command (Ctrl+C), like its process token.</param>
    /// <returns>How it ended.</returns>
    Task<RemoteProcessExit> RunTerminalAsync(
        RemoteProcessLaunch launch, TerminalExecutionContext terminal, CancellationToken cancellationToken);

    /// <summary>
    /// Starts one service in a worker of its own and returns at once; the returned task ends with the
    /// service's run (ADR 0070, section 3).
    /// </summary>
    /// <param name="launch">The process to run.</param>
    /// <returns>The running service.</returns>
    IRemoteService StartService(RemoteProcessLaunch launch);

    /// <summary>
    /// Gets whether this host runs background tasks (ADR 0128, section 3); when it does not, the OS
    /// runs them on the main thread (section 5). <see langword="false"/> by default, so existing hosts
    /// keep compiling.
    /// </summary>
    bool CanRunBackgroundJobs => false;

    /// <summary>
    /// Runs one background task (ADR 0128) in a worker of the pool until it ends. The task's progress
    /// reaches <see cref="RemoteBackgroundJobLaunch.Progress"/>; its result or its fault comes back in
    /// <see cref="RemoteProcessExit.JobResult"/> or <see cref="RemoteProcessExit.JobFault"/>.
    /// </summary>
    /// <param name="launch">The task's process and request.</param>
    /// <param name="cancellationToken">Cancels the task: its process token in the worker is cancelled.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="NotSupportedException">The host does not run background tasks (<see cref="CanRunBackgroundJobs"/>).</exception>
    Task<RemoteProcessExit> RunBackgroundJobAsync(RemoteBackgroundJobLaunch launch, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This host does not run background tasks in workers.");
}

/// <summary>One background task the OS hands to a worker (ADR 0128).</summary>
/// <param name="Context">The task's process's trusted context on the OS side: every remote SDK call is served through it.</param>
/// <param name="Control">The task's process's control block (stop request, kill, ADR 0069).</param>
/// <param name="Request">The task (already checked by the OS) and its input.</param>
/// <param name="Progress">Receives the task's reports, or <see langword="null"/>.</param>
public sealed record RemoteBackgroundJobLaunch(
    IAppExecutionContext Context,
    ProcessControlBlock? Control,
    Kazo.Os.AppSdk.Threading.HackBackgroundJobRequest Request,
    Action<Kazo.Os.AppSdk.Threading.HackBackgroundProgress>? Progress);

/// <summary>One process the OS hands to a worker.</summary>
/// <param name="Descriptor">The app's resolved entry point (its assembly and type are loaded again in the worker).</param>
/// <param name="Context">The process's trusted context on the OS side: every remote SDK call is served through it.</param>
/// <param name="Control">The process's control block (stop request, kill, ADR 0069), or <see langword="null"/> outside the trusted factory.</param>
/// <param name="PlacementGroup">
/// The pipeline the process belongs to, if any: every stage of one pipeline goes to the same worker
/// (ADR 0070, section 2).
/// </param>
public sealed record RemoteProcessLaunch(
    AppDescriptor Descriptor,
    IAppExecutionContext Context,
    ProcessControlBlock? Control,
    Guid? PlacementGroup);

/// <summary>How a remote process ended, mapped by the orchestrator to the codes a local one would give.</summary>
/// <param name="Status">The kind of ending.</param>
/// <param name="ExitCode">The command's own exit code when <see cref="Status"/> is <see cref="RemoteProcessExitStatus.Exited"/>.</param>
/// <param name="FaultMessage">The escaping exception's message when <see cref="Status"/> is <see cref="RemoteProcessExitStatus.Faulted"/>.</param>
public sealed record RemoteProcessExit(RemoteProcessExitStatus Status, int ExitCode = 0, string? FaultMessage = null)
{
    /// <summary>Gets the result of a background task that returned (ADR 0128), as JSON; <see langword="null"/> otherwise.</summary>
    public string? JobResult { get; init; }

    /// <summary>Gets what escaped a background task (ADR 0128, section 4), or <see langword="null"/>.</summary>
    public Kazo.Os.AppSdk.Threading.HackBackgroundJobException? JobFault { get; init; }

    /// <summary>
    /// Gets whether the worker refused the background task before running it (a target or an input it
    /// could not read): the caller sees an <see cref="ArgumentException"/>, not a fault of the task.
    /// </summary>
    public bool JobRejected { get; init; }

    /// <summary>A normal exit with <paramref name="code"/>.</summary>
    public static RemoteProcessExit Exited(int code) => new(RemoteProcessExitStatus.Exited, code);
}

/// <summary>The kind of ending of a remote process.</summary>
public enum RemoteProcessExitStatus
{
    /// <summary>The entry point returned an exit code.</summary>
    Exited,

    /// <summary><c>SIGKILL</c>: killed by the OS, or its worker was terminated (exit code 137).</summary>
    Killed,

    /// <summary>Interrupted: its token was cancelled or it honored a stop request (exit code 130).</summary>
    Cancelled,

    /// <summary>The reader of its output went away and it let the "Broken pipe" error escape (exit code 141).</summary>
    BrokenPipe,

    /// <summary>An exception escaped its entry point.</summary>
    Faulted
}

/// <summary>A service running in a worker of its own.</summary>
public interface IRemoteService
{
    /// <summary>Gets the task that ends with the service's run (faulted when it failed).</summary>
    Task Completion { get; }

    /// <summary>
    /// Runs the service's <c>OnStoppingAsync</c> in its worker (logout, shutdown, disable), like
    /// <c>ServiceAppBase.StopAsync</c> for a local service.
    /// </summary>
    /// <param name="reason">Why the OS stops it.</param>
    /// <param name="cancellationToken">The stop budget.</param>
    Task StopAsync(ServiceStopReason reason, CancellationToken cancellationToken);
}

/// <summary>
/// Groups the processes started together, so a remote host can place every stage of one pipeline on
/// the same worker (ADR 0070, section 2). The shell opens a group around each multi-stage pipeline;
/// the group flows with the async context into every stage's launch.
/// </summary>
public static class ExecutionPlacement
{
    private static readonly AsyncLocal<Guid?> s_group = new();

    /// <summary>Gets the group of the code running here, or <see langword="null"/> outside any pipeline.</summary>
    public static Guid? CurrentGroup => s_group.Value;

    /// <summary>Opens a new group until the returned scope is disposed.</summary>
    /// <returns>The scope; disposing it restores the previous group.</returns>
    public static IDisposable BeginGroup()
    {
        Guid? previous = s_group.Value;
        s_group.Value = Guid.NewGuid();
        return new Scope(previous);
    }

    private sealed class Scope(Guid? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                s_group.Value = previous;
            }
        }
    }
}

/// <summary>
/// An exception escaped a command's entry point in its worker (ADR 0070). The worker's exception
/// cannot cross the message boundary, so the OS rethrows its message: the launch ends exactly like a
/// local entry-point fault (<c>lifecycle.entry-point.fault</c>).
/// </summary>
public sealed class RemoteProcessFaultException : Exception
{
    /// <summary>Initializes the exception with the worker-side message.</summary>
    /// <param name="message">The original exception's type and message.</param>
    public RemoteProcessFaultException(string message)
        : base(message)
    {
    }
}
