using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Processes;

public sealed class ResourceProfileTests
{
    [Fact]
    public void A_burst_weight_below_its_baseline_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourceProfile(0.5, 0.2, 0, 0, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Weights_outside_zero_to_one_are_rejected(double invalidWeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourceProfile(invalidWeight, invalidWeight, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void None_carries_zero_weight_for_every_dimension()
    {
        ResourceProfile profile = ResourceProfile.None;

        Assert.Equal(0, profile.BaselineCpuWeight);
        Assert.Equal(0, profile.BurstNetworkIoWeight);
    }
}

public sealed class ProcessRecordTests
{
    private static ProcessRecord CreateRunning(DateTimeOffset createdAtUtc, DateTimeOffset startedAtUtc) => new(
        ProcessId.FromInt64(1),
        parentPid: null,
        "com.hackeros.terminal",
        AppInstanceId.FromGuid(Guid.NewGuid()),
        AppKind.Terminal,
        LocalUserId.FromGuid(Guid.NewGuid()),
        SessionId.FromGuid(Guid.NewGuid()),
        ProcessState.Running,
        ResourceProfile.None,
        serviceHealth: null,
        createdAtUtc,
        startedAtUtc,
        stoppedAtUtc: null,
        exitCode: null,
        exitReason: null);

    [Fact]
    public void A_running_process_requires_a_start_timestamp()
    {
        Assert.Throws<ArgumentException>(() => new ProcessRecord(
            ProcessId.FromInt64(1),
            parentPid: null,
            "com.hackeros.terminal",
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Terminal,
            LocalUserId.FromGuid(Guid.NewGuid()),
            SessionId.FromGuid(Guid.NewGuid()),
            ProcessState.Running,
            ResourceProfile.None,
            serviceHealth: null,
            DateTimeOffset.UnixEpoch,
            startedAtUtc: null,
            stoppedAtUtc: null,
            exitCode: null,
            exitReason: null));
    }

    [Fact]
    public void A_terminal_process_requires_a_stop_timestamp_and_exit_reason()
    {
        Assert.Throws<ArgumentException>(() => new ProcessRecord(
            ProcessId.FromInt64(1),
            parentPid: null,
            "com.hackeros.terminal",
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Terminal,
            LocalUserId.FromGuid(Guid.NewGuid()),
            SessionId.FromGuid(Guid.NewGuid()),
            ProcessState.Stopped,
            ResourceProfile.None,
            serviceHealth: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            stoppedAtUtc: null,
            exitCode: null,
            exitReason: null));
    }

    [Fact]
    public void A_non_terminal_process_cannot_carry_a_stop_timestamp()
    {
        Assert.Throws<ArgumentException>(() => new ProcessRecord(
            ProcessId.FromInt64(1),
            parentPid: null,
            "com.hackeros.terminal",
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Terminal,
            LocalUserId.FromGuid(Guid.NewGuid()),
            SessionId.FromGuid(Guid.NewGuid()),
            ProcessState.Running,
            ResourceProfile.None,
            serviceHealth: null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            stoppedAtUtc: DateTimeOffset.UnixEpoch,
            exitCode: null,
            exitReason: null));
    }

    [Fact]
    public void A_process_cannot_be_its_own_parent()
    {
        ProcessId pid = ProcessId.FromInt64(1);

        Assert.Throws<ArgumentException>(() => new ProcessRecord(
            pid,
            pid,
            "com.hackeros.terminal",
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Terminal,
            LocalUserId.FromGuid(Guid.NewGuid()),
            SessionId.FromGuid(Guid.NewGuid()),
            ProcessState.Created,
            ResourceProfile.None,
            serviceHealth: null,
            DateTimeOffset.UnixEpoch,
            startedAtUtc: null,
            stoppedAtUtc: null,
            exitCode: null,
            exitReason: null));
    }

    [Fact]
    public void IsTerminal_is_true_only_for_Stopped_or_Faulted()
    {
        ProcessRecord running = CreateRunning(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Assert.False(running.IsTerminal);
    }

    [Fact]
    public void Zero_is_not_a_valid_process_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessId.FromInt64(0));
    }
}
