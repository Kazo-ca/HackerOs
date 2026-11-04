using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Provides one app instance's authorized process/job access. An app may always observe and
/// stop its own process; observing or managing other processes requires
/// <see cref="AppCapabilities.ProcessList"/>/<see cref="AppCapabilities.ProcessManage"/>.
/// </summary>
public sealed class AppProcessGateway : IAppProcessGateway
{
    private readonly IProcessManager _processManager;
    private readonly ICapabilityChecker _capabilities;

    /// <summary>Initializes a process gateway bound to the process hosting this app instance.</summary>
    public AppProcessGateway(IProcessManager processManager, ICapabilityChecker capabilities, ProcessId ownPid)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        OwnPid = ownPid;
    }

    private ProcessId OwnPid { get; }

    /// <summary>Gets the limit table behind <see cref="SetResourceLimitAsync"/> (ADR 0125); without it the call is not supported.</summary>
    public IProcessResourceLimitTable? ResourceLimits { get; init; }

    /// <summary>
    /// Gets whether the app runs as an elevated root (<c>sudo</c>): it may raise hard limits and act on any
    /// user's process, and lower a niceness or renice another user's process (ADR 0127).
    /// </summary>
    public bool IsRootElevated { get; init; }

    /// <inheritdoc />
    public ProcessRecord OwnProcess =>
        _processManager.TryGetActive(OwnPid, out ProcessRecord process)
            ? process
            : throw new InvalidOperationException("The hosting process is no longer active.");

    /// <inheritdoc />
    public ProcessRecord StartChild(string appId, AppInstanceId appInstanceId, AppKind kind, ResourceProfile resourceProfile)
    {
        ProcessRecord own = OwnProcess;
        ProcessStartRequest request = new(OwnPid, appId, appInstanceId, kind, own.UserId, own.SessionId, resourceProfile);
        return _processManager.Start(request);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessRecord> ListProcesses()
    {
        IReadOnlyList<ProcessRecord> all = _processManager.GetActiveProcesses();
        if (_capabilities.Evaluate(AppCapabilities.ProcessList).Granted)
        {
            return all;
        }

        // Its own process and its children (ADR 0140: its HackBackground tasks).
        return [.. all.Where(p => p.Pid == OwnPid || IsDescendant(p.Pid))];
    }

    /// <inheritdoc />
    public async Task<ProcessRecord> StopAsync(
        ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default)
    {
        RequireManageUnlessOwn(pid);
        return await _processManager.StopAsync(pid, timeout, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed)
    {
        RequireManageUnlessOwn(pid);
        return _processManager.Kill(pid, reason);
    }

    /// <inheritdoc />
    public int GetNice(ProcessId pid)
    {
        if (pid != OwnPid)
        {
            _capabilities.Require(AppCapabilities.ProcessList);
        }

        return _processManager.GetNice(pid);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The checks of Linux's <c>setpriority(2)</c>: <c>EPERM</c> for another user's process, and
    /// <c>EACCES</c> for a lower value, unless the caller is root. Both surface as
    /// <see cref="UnauthorizedAccessException"/>.
    /// </remarks>
    public int SetNice(ProcessId pid, int nice)
    {
        RequireManageUnlessOwn(pid);
        if (!_processManager.TryGetActive(pid, out ProcessRecord target))
        {
            throw new KeyNotFoundException($"No active process with PID '{pid}'.");
        }

        int requested = ProcessNice.Clamp(nice);
        if (!IsRootElevated)
        {
            if (target.UserId != OwnProcess.UserId)
            {
                throw new UnauthorizedAccessException("Operation not permitted");
            }

            if (requested < _processManager.GetNice(pid))
            {
                throw new UnauthorizedAccessException("Permission denied");
            }
        }

        return _processManager.SetNice(pid, requested);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ADR 0125 section 4, the rules of <c>prlimit(2)</c>: the app's own process, or another process of
    /// the same user with <see cref="AppCapabilities.ProcessManage"/>, or any process when elevated.
    /// </remarks>
    public Task<ResourceLimitChangeResult> SetResourceLimitAsync(
        ProcessId pid, ResourceLimitKind kind, ResourceLimit limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ResourceLimits is not { } table)
        {
            throw new NotSupportedException("This host does not manage resource limits.");
        }

        RequireManageUnlessOwn(pid);
        if (!_processManager.TryGetActive(pid, out ProcessRecord target))
        {
            return Task.FromResult(ResourceLimitChangeResult.NoSuchProcess);
        }

        if (!IsRootElevated && pid != OwnPid && target.UserId != OwnProcess.UserId)
        {
            return Task.FromResult(ResourceLimitChangeResult.PermissionDenied);
        }

        return Task.FromResult(table.TrySet(pid, kind, limit, privileged: IsRootElevated));
    }

    private void RequireManageUnlessOwn(ProcessId pid)
    {
        if (pid != OwnPid && !IsDescendant(pid))
        {
            _capabilities.Require(AppCapabilities.ProcessManage);
        }
    }

    /// <summary>
    /// ADR 0140: an app may stop or kill its own children (its HackBackground tasks), as a Unix
    /// parent signals the processes it started, without <see cref="AppCapabilities.ProcessManage"/>.
    /// </summary>
    private bool IsDescendant(ProcessId pid)
    {
        // Bounded walk up the parent chain: a process table never holds a cycle, but stay safe.
        for (int depth = 0; depth < 64 && _processManager.TryGetActive(pid, out ProcessRecord process); depth++)
        {
            if (process.ParentPid is not { } parent)
            {
                return false;
            }

            if (parent == OwnPid)
            {
                return true;
            }

            pid = parent;
        }

        return false;
    }
}
