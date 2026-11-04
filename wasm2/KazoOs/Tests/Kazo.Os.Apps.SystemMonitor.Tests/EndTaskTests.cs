using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Apps.SystemMonitor.Processes;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Apps.SystemMonitor.Tests;

/// <summary>ADR 0069, section 4: "End task" is a SIGTERM with 5 seconds of grace, "Force kill" a SIGKILL.</summary>
public sealed class EndTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly SessionId Session = SessionId.FromGuid(Guid.NewGuid());

    [Fact]
    public async Task End_task_terminates_politely_and_force_kill_kills_at_once()
    {
        FakeAppProcessGateway processes = new FakeAppProcessGateway()
            .WithProcess(ProcessRecordFactory.Running(42, "org.kazoos.text-editor", Session, Now, Now));
        using IDisposable scope = Enter(processes);
        KazoProcess editor = await KazoProcess.GetProcessByIdAsync(42);

        await EndTaskRunner.End(editor, EndTaskMode.Terminate);
        await EndTaskRunner.End(editor, EndTaskMode.ForceKill);

        Assert.Equal([(ProcessId.FromInt64(42), false), (ProcessId.FromInt64(42), true)], processes.Terminations);
        Assert.Equal(
            (ProcessId.FromInt64(42), TimeSpan.FromSeconds(5), ProcessExitReason.Terminated),
            Assert.Single(processes.StopRequests));
    }

    [Fact]
    public async Task The_selection_is_signalled_together_and_reported_in_its_order()
    {
        TaskCompletionSource slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<long> started = [];
        ProcessRow first = Row(10);
        ProcessRow second = Row(11);

        Task<EndTaskReport> ending = EndTaskRunner.EndAsync([first, second], row =>
        {
            started.Add(row.Pid);
            return row.Pid == 10 ? slow.Task : Task.CompletedTask;
        });

        // Process 11 was signalled while 10 was still in its grace period.
        Assert.Equal([10L, 11], started);
        Assert.False(ending.IsCompleted);
        slow.SetResult();
        EndTaskReport report = await ending;
        Assert.Equal([10L, 11], report.Ended.Select(static row => row.Pid));
    }

    private static IDisposable Enter(FakeAppProcessGateway processes)
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/proc/42")
            .WithFile("/proc/42/status", "Name:\ttext-editor\nAppId:\torg.kazoos.text-editor\nState:\tR (running)\nPPid:\t0\n");
        return KazoExecutionContext.Enter(new MinimalAppExecutionContext(
            AppDefinitionMapper.ToManifest(new SystemMonitorApp()), fileSystem: fileSystem, processes: processes));
    }

    private static ProcessRow Row(long pid) => new(pid, null, $"p{pid}", "org.tests.app", "alice", "Running", 'R', 0, 0, 0, 0);
}
