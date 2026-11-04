using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Tests.Support;

/// <summary>In-memory <see cref="IAppProcessGateway"/> double for command tests.</summary>
public sealed class FakeAppProcessGateway : IAppProcessGateway
{
    private readonly Dictionary<long, ProcessRecord> _processes = new();
    private ProcessRecord? _ownProcess;

    /// <summary>Records every termination request this fake received, in call order.</summary>
    public List<(ProcessId Pid, bool Forced)> Terminations { get; } = [];

    /// <summary>Records the grace period and reason of every graceful stop request (ADR 0069), in call order.</summary>
    public List<(ProcessId Pid, TimeSpan Timeout, ProcessExitReason Reason)> StopRequests { get; } = [];

    /// <summary>PIDs on which the next termination call should throw <see cref="AppGatewayAccessDeniedException"/>.</summary>
    public HashSet<long> DeniedPids { get; } = [];

    public FakeAppProcessGateway WithProcess(ProcessRecord record)
    {
        _processes[record.Pid.Value] = record;
        return this;
    }

    public FakeAppProcessGateway WithOwnProcess(ProcessRecord record)
    {
        _ownProcess = record;
        return WithProcess(record);
    }

    public ProcessRecord OwnProcess =>
        _ownProcess ?? throw new NotSupportedException("OwnProcess was not seeded on this FakeAppProcessGateway.");

    public ProcessRecord StartChild(string appId, AppInstanceId appInstanceId, AppKind kind, ResourceProfile resourceProfile) =>
        throw new NotSupportedException("StartChild is not implemented by this fake.");

    public IReadOnlyList<ProcessRecord> ListProcesses() => _processes.Values.ToList();

    public Task<ProcessRecord> StopAsync(
        ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDenied(pid);
        if (!_processes.TryGetValue(pid.Value, out ProcessRecord? record))
        {
            throw new KeyNotFoundException($"No active process with PID {pid.Value}.");
        }
        Terminations.Add((pid, false));
        StopRequests.Add((pid, timeout, reason));
        return Task.FromResult(record);
    }

    public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed)
    {
        EnsureNotDenied(pid);
        if (!_processes.TryGetValue(pid.Value, out ProcessRecord? record))
        {
            throw new KeyNotFoundException($"No active process with PID {pid.Value}.");
        }
        Terminations.Add((pid, true));
        return record;
    }

    private void EnsureNotDenied(ProcessId pid)
    {
        if (DeniedPids.Contains(pid.Value))
        {
            throw new AppGatewayAccessDeniedException("process.manage", CapabilityPolicyEvaluation.DenyMissing(1));
        }
    }
}
