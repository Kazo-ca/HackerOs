using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Runs the background tasks of <see cref="KazoBackground"/> (ADR 0128, ADR 0140): each one is a
/// child process of the app that asked, with the same user, session and granted capabilities, named
/// <c>&lt;app command&gt;:&lt;task&gt;</c>. It goes to a Web Worker of the pool when the host has some,
/// otherwise it runs on the main thread, with the same serialization of its input and result.
/// </summary>
/// <remarks>
/// <para>
/// Like <c>fork</c> + <c>exec</c> of a helper binary, the OS creates the process, its trusted
/// context and its control block here, on its own thread; only the task's code runs elsewhere. The
/// process ends with the task, ends with the app (its parent; a stop request, then a kill after
/// <see cref="ParentExitGracePeriod"/>, ADR 0147), and can be killed alone from
/// <c>kill</c> or the task manager without closing the window.
/// </para>
/// <para>
/// At most <see cref="MaxConcurrentJobsPerApp"/> tasks of one app run at once for one user; the next
/// ones wait for a place, in order (ADR 0128, section 3).
/// </para>
/// </remarks>
public sealed class BackgroundJobService
{
    private readonly IProcessManager _processManager;
    private readonly Func<string, AppDescriptor?>? _descriptorOf;
    private readonly Func<IRemoteProcessHost?>? _remoteHost;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProcessId, BackgroundJobInfo> _jobs = new();

    /// <summary>Initializes the service.</summary>
    /// <param name="processManager">The process table the tasks' processes are created in.</param>
    /// <param name="descriptorOf">
    /// Finds an app's resolved descriptor, to check that a task lives in the app's own assembly
    /// (ADR 0128, section 2) and to find that assembly on the main thread; <see langword="null"/>
    /// skips the check and looks for the assembly among the loaded ones.
    /// </param>
    /// <param name="remoteHost">
    /// Lazily resolves the out-of-thread host (ADR 0070); <see langword="null"/> or a host that cannot
    /// run tasks keeps every task on the main thread (ADR 0128, section 5).
    /// </param>
    /// <param name="maxConcurrentJobsPerApp">
    /// Tasks of one app run at once for one user; the number of virtual cores when <see langword="null"/>.
    /// </param>
    public BackgroundJobService(
        IProcessManager processManager,
        Func<string, AppDescriptor?>? descriptorOf = null,
        Func<IRemoteProcessHost?>? remoteHost = null,
        int? maxConcurrentJobsPerApp = null)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _descriptorOf = descriptorOf;
        _remoteHost = remoteHost;
        MaxConcurrentJobsPerApp = maxConcurrentJobsPerApp is { } max
            ? Math.Max(1, max)
            : Math.Max(1, (int)Math.Ceiling(VirtualHardwareProfile.Default.CpuCapacity));
    }

    /// <summary>Gets how many tasks of one app may run at once for one user (ADR 0128, section 3).</summary>
    public int MaxConcurrentJobsPerApp { get; }

    /// <summary>
    /// How long a task may still run once its app has ended without being killed (ADR 0147): a stop
    /// request, then a kill, as <c>SIGTERM</c> then <c>SIGKILL</c>.
    /// </summary>
    public static readonly TimeSpan ParentExitGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>Gets the tasks running now (<c>/proc/&lt;pid&gt;/status</c>, the task manager).</summary>
    public IReadOnlyList<BackgroundJobInfo> GetJobs() => [.. _jobs.Values.OrderBy(static job => job.Pid.Value)];

    /// <summary>Finds a running task by its process.</summary>
    /// <param name="pid">A process.</param>
    /// <param name="job">The task, when <paramref name="pid"/> is one.</param>
    public bool TryGetJob(ProcessId pid, out BackgroundJobInfo job) => _jobs.TryGetValue(pid, out job!);

    /// <summary>The name a task's process carries: <c>&lt;app command&gt;:&lt;task&gt;</c> (ADR 0128, section 3).</summary>
    /// <param name="manifest">The calling app.</param>
    /// <param name="task">The task's name.</param>
    public static string ProcessNameOf(AppManifest manifest, string task)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string command = manifest.Terminal?.Name ?? manifest.Id[(manifest.Id.LastIndexOf('.') + 1)..];
        return $"{command}:{task}";
    }

    /// <summary>Runs one task for <paramref name="caller"/> until it ends (the OS side of <see cref="IAppBackgroundGateway"/>).</summary>
    /// <param name="caller">The calling app's trusted context.</param>
    /// <param name="createChildContext">Builds the trusted context of the task's process, with the caller's identity and capabilities.</param>
    /// <param name="request">The task.</param>
    /// <param name="progress">Receives its throttled reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>The result as JSON, or <see langword="null"/>.</returns>
    internal async Task<string?> RunAsync(
        IAppExecutionContext caller,
        Func<ProcessRecord, IAppExecutionContext> createChildContext,
        KazoBackgroundJobRequest request,
        Action<KazoBackgroundProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(createChildContext);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // ADR 0128, sections 2 and 4: the OS checks again what the analyzer checked at build time.
        Assembly? assembly = CheckRequest(caller.Manifest, request);
        string name = ProcessNameOf(caller.Manifest, NameOf(request));
        int nice = Math.Clamp(request.Nice, -20, 19);
        if (nice < 0 && caller.UserAuthority == AppAuthority.User && !caller.IsRootElevated)
        {
            // nice(2): only the administrator may raise a priority.
            nice = 0;
        }

        // ConfigureAwait(false) throughout: this bookkeeping is the OS's, not the caller's. The caller's
        // scheduler drops its continuations once it is killed (ADR 0069), and the task's process must
        // still be ended and its place released then. The task's own code runs on its own scheduler.
        SemaphoreSlim gate = _gates.GetOrAdd($"{caller.UserId}\n{caller.Manifest.Id}", _ => new SemaphoreSlim(MaxConcurrentJobsPerApp));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessRecord parent = caller.Processes.OwnProcess;
            ProcessRecord process = _processManager.Start(new ProcessStartRequest(
                parent.Pid,
                caller.Manifest.Id,
                AppInstanceId.FromGuid(Guid.NewGuid()),
                AppKind.Terminal,
                parent.UserId,
                parent.SessionId,
                parent.ResourceProfile,
                // argv[0] is the task's name, as ps and /proc/<pid>/cmdline show it; the environment
                // and working directory are the app's, copied by the process manager (ADR 0050).
                [name])
            {
                // ADR 0127: the task's own niceness, shown by ps and /proc, and its place in the pool queue.
                Nice = nice,
                // ADR 0147: when the app ends, the task gets a stop request and 5 s to end, then is killed.
                ParentExitGracePeriod = ParentExitGracePeriod,
            });
            IAppExecutionContext child = createChildContext(process);
            IRemoteProcessHost? remote = _remoteHost?.Invoke() is { CanRunBackgroundJobs: true } host ? host : null;
            BackgroundJobInfo info = new(
                process.Pid, parent.Pid, caller.Manifest.Id, name,
                remote is null ? BackgroundJobHost.MainThread : BackgroundJobHost.Worker, nice);
            _jobs[process.Pid] = info;
            try
            {
                _processManager.MarkRunning(process.Pid);
                string? result = remote is null
                    ? await RunOnMainThreadAsync(child, assembly, request, progress, cancellationToken).ConfigureAwait(false)
                    : await RunInWorkerAsync(remote, child, request with { Name = name, Nice = nice }, progress, cancellationToken).ConfigureAwait(false);
                End(process.Pid, 0);
                return result;
            }
            catch (KazoProcessKilledException)
            {
                // Usually already ended by the kill; a worker that died takes the process with it (128 + 9).
                ProcessExitReason reason = KillReasonOf(process.Pid);
                End(process.Pid, 137);
                throw new KazoProcessKilledException(process.Pid.Value, reason);
            }
            catch (OperationCanceledException)
            {
                End(process.Pid, 130);
                throw;
            }
            catch (ArgumentException)
            {
                // The task's process found nothing valid to run: like exec(2) failing, exit code 127.
                End(process.Pid, 127);
                throw;
            }
            catch (Exception) when (_processManager.TryGetActive(process.Pid, out _))
            {
                End(process.Pid, 1);
                throw;
            }
            finally
            {
                _jobs.TryRemove(process.Pid, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// ADR 0128, section 5: no worker, the same contract on the main thread. The task runs as its own
    /// process (its scheduler, its context), its input and result still cross as JSON.
    /// </summary>
    private static async Task<string?> RunOnMainThreadAsync(
        IAppExecutionContext child,
        Assembly? assembly,
        KazoBackgroundJobRequest request,
        Action<KazoBackgroundProgress>? progress,
        CancellationToken cancellationToken)
    {
        Assembly resolved = assembly ?? FindLoadedAssembly(request.Target.AssemblyName)
            ?? throw new ArgumentException($"The assembly '{request.Target.AssemblyName}' of the background task is not loaded.", nameof(request));
        KazoBackgroundJobRunner.PreparedJob job = KazoBackgroundJobRunner.Prepare(resolved, request.Target, request.InputJson);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(child.CancellationToken, cancellationToken);
        KazoBackgroundContext context = new(child.ProcessId.Value, linked.Token, progress);
        using IDisposable ambient = KazoExecutionContext.Enter(child);
        return child is AppExecutionContext trusted
            ? await trusted.Control.RunAsync(() => KazoBackgroundFaults.RunAsync(job, context)).ConfigureAwait(false)
            : await KazoBackgroundFaults.RunAsync(job, context).ConfigureAwait(false);
    }

    /// <summary>The task in a Web Worker of the pool (ADR 0128, section 3), and how it ended.</summary>
    private static async Task<string?> RunInWorkerAsync(
        IRemoteProcessHost remote,
        IAppExecutionContext child,
        KazoBackgroundJobRequest request,
        Action<KazoBackgroundProgress>? progress,
        CancellationToken cancellationToken)
    {
        RemoteProcessExit exit = await remote.RunBackgroundJobAsync(
            new RemoteBackgroundJobLaunch(child, child is AppExecutionContext trusted ? trusted.Control : null, request, progress),
            cancellationToken).ConfigureAwait(false);
        switch (exit.Status)
        {
            case RemoteProcessExitStatus.Exited when exit.ExitCode == 0:
                return exit.JobResult;
            case RemoteProcessExitStatus.Cancelled:
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The background task honored its stop request.", child.CancellationToken);
            case RemoteProcessExitStatus.Killed:
                throw new KazoProcessKilledException(child.ProcessId.Value);
            default:
                if (exit.JobRejected)
                {
                    throw new ArgumentException(exit.JobFault?.Message ?? exit.FaultMessage ?? "The worker refused the background task.", nameof(request));
                }

                throw exit.JobFault ?? new KazoBackgroundJobException(
                    typeof(InvalidOperationException).FullName!,
                    exit.FaultMessage ?? $"The background task's process exited with code {exit.ExitCode}.",
                    originalStackTrace: null);
        }
    }

    /// <summary>
    /// The checks of ADR 0128, sections 2 and 4: the task lives in the calling app's own assembly and
    /// its input is at most 16 MiB of JSON.
    /// </summary>
    /// <returns>The app's assembly when it is known here, otherwise <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    private Assembly? CheckRequest(AppManifest manifest, KazoBackgroundJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.InputJson);
        if (Encoding.UTF8.GetByteCount(request.InputJson) > MaxPayloadBytes)
        {
            throw new ArgumentException("The input of a background task is limited to 16 MiB of JSON (ADR 0128, section 4).", nameof(request));
        }

        if (request.Name is { } named && (string.IsNullOrWhiteSpace(named) || named.Length > 64 || named.Any(char.IsControl) || named.Contains(':')))
        {
            throw new ArgumentException($"'{named}' is not a valid name for a background task: 1 to 64 characters, no ':' and no control character.", nameof(request));
        }

        if (_descriptorOf?.Invoke(manifest.Id) is not { } descriptor)
        {
            return null;
        }

        string? appAssembly = descriptor.Assembly.GetName().Name;
        if (!string.Equals(appAssembly, request.Target.AssemblyName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The background task '{request.Target.TypeName}' is in '{request.Target.AssemblyName}', not in the assembly " +
                $"'{appAssembly}' of the app '{manifest.Id}' (ADR 0128, section 2).", nameof(request));
        }

        return descriptor.Assembly;
    }

    /// <summary>The largest input or result of a task, in UTF-8 bytes of JSON (the largest file, ADR 0094).</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    private static string NameOf(KazoBackgroundJobRequest request) => request.Name ?? request.Target.DisplayName;

    private static Assembly? FindLoadedAssembly(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal));

    private ProcessExitReason KillReasonOf(ProcessId pid) =>
        _processManager.GetHistory().LastOrDefault(record => record.Pid == pid)?.ExitReason ?? ProcessExitReason.Killed;

    private void End(ProcessId pid, int exitCode)
    {
        if (!_processManager.TryGetActive(pid, out _))
        {
            return;
        }

        try
        {
            _processManager.Complete(pid, exitCode);
        }
        catch (KeyNotFoundException)
        {
            // Killed in the meantime.
        }
    }
}

/// <summary>A background task running now (ADR 0128, section 3).</summary>
/// <param name="Pid">Its process.</param>
/// <param name="ParentPid">The app that started it.</param>
/// <param name="AppId">The app's ID (the process carries it, as a thread carries its program).</param>
/// <param name="Name">Its process name: <c>&lt;app command&gt;:&lt;task&gt;</c>.</param>
/// <param name="Host">Where its code runs.</param>
/// <param name="Nice">Its <c>nice</c> value (ADR 0127).</param>
public sealed record BackgroundJobInfo(ProcessId Pid, ProcessId ParentPid, string AppId, string Name, BackgroundJobHost Host, int Nice);

/// <summary>Where a background task runs (<c>Host:</c> of <c>/proc/&lt;pid&gt;/status</c>).</summary>
public enum BackgroundJobHost
{
    /// <summary>On the OS's main thread: the host has no worker (ADR 0128, section 5).</summary>
    MainThread,

    /// <summary>In a Web Worker of the pool.</summary>
    Worker
}

/// <summary>
/// One app instance's <see cref="IAppBackgroundGateway"/>, bound by the trusted factory: it forwards
/// to <see cref="BackgroundJobService"/> with the caller's own context, never one the app supplies.
/// </summary>
internal sealed class AppBackgroundGateway(
    Func<BackgroundJobService> service,
    Func<IAppExecutionContext> caller,
    Func<ProcessRecord, IAppExecutionContext> createChildContext) : IAppBackgroundGateway
{
    /// <inheritdoc />
    public Task<string?> RunAsync(KazoBackgroundJobRequest request, Action<KazoBackgroundProgress>? progress, CancellationToken cancellationToken) =>
        service().RunAsync(caller(), createChildContext, request, progress, cancellationToken);
}
