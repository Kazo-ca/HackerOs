using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Kill;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class KillCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionId Session = SessionId.FromGuid(Guid.NewGuid());

    [Fact]
    public async Task ExecuteAsync_WithoutFlag_StopsGracefully()
    {
        FakeAppProcessGateway processes = new FakeAppProcessGateway()
            .WithProcess(ProcessRecordFactory.Running(7, "some-app", Session, Now, Now));
        TerminalExecutionContext context = CreateContext(processes, "7");

        int exitCode = await new KillCommand(AppDefinitionMapper.ToManifest(new KillApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        (ProcessId pid, bool forced) = Assert.Single(processes.Terminations);
        Assert.Equal(7, pid.Value);
        Assert.False(forced);
        // ADR 0069: SIGTERM from another process, forced after 5 s.
        Assert.Equal((pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated), Assert.Single(processes.StopRequests));
    }

    [Theory]
    [InlineData("-15")]
    [InlineData("-TERM")]
    [InlineData("-SIGTERM")]
    public async Task ExecuteAsync_WithTermSignal_SendsSigterm(string signal)
    {
        FakeAppProcessGateway processes = new FakeAppProcessGateway()
            .WithProcess(ProcessRecordFactory.Running(7, "some-app", Session, Now, Now));
        TerminalExecutionContext context = CreateContext(processes, signal, "7");

        int exitCode = await new KillCommand(AppDefinitionMapper.ToManifest(new KillApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(ProcessExitReason.Terminated, Assert.Single(processes.StopRequests).Reason);
    }

    [Fact]
    public async Task ExecuteAsync_WithDash9_ForceKills()
    {
        FakeAppProcessGateway processes = new FakeAppProcessGateway()
            .WithProcess(ProcessRecordFactory.Running(7, "some-app", Session, Now, Now));
        TerminalExecutionContext context = CreateContext(processes, "-9", "7");

        int exitCode = await new KillCommand(AppDefinitionMapper.ToManifest(new KillApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        (ProcessId pid, bool forced) = Assert.Single(processes.Terminations);
        Assert.Equal(7, pid.Value);
        Assert.True(forced);
        // SIGKILL: no stop request, no grace period.
        Assert.Empty(processes.StopRequests);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownPid_ReportsNoSuchProcess()
    {
        FakeAppProcessGateway processes = new();
        TerminalExecutionContext context = CreateContext(processes, "99");

        int exitCode = await new KillCommand(AppDefinitionMapper.ToManifest(new KillApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("No such process", context.StandardError.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_CapabilityDenied_SurfacesRealMessage()
    {
        FakeAppProcessGateway processes = new FakeAppProcessGateway()
            .WithProcess(ProcessRecordFactory.Running(7, "some-app", Session, Now, Now));
        processes.DeniedPids.Add(7);
        TerminalExecutionContext context = CreateContext(processes, "7");

        int exitCode = await new KillCommand(AppDefinitionMapper.ToManifest(new KillApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("process.manage", context.StandardError.ToString());
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppProcessGateway processes, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new KillApp()), processes: processes),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());
}
