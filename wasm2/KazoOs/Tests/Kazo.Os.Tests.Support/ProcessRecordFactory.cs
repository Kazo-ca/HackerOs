using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Tests.Support;

/// <summary>Builds minimal, valid <see cref="ProcessRecord"/> instances for command tests.</summary>
public static class ProcessRecordFactory
{
    public static ProcessRecord Running(
        long pid,
        string appId,
        SessionId sessionId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset createdAtUtc,
        LocalUserId? userId = null,
        AppKind kind = AppKind.Terminal) => new(
        ProcessId.FromInt64(pid),
        parentPid: null,
        appId,
        AppInstanceId.FromGuid(Guid.NewGuid()),
        kind,
        userId ?? LocalUserId.FromGuid(Guid.NewGuid()),
        sessionId,
        ProcessState.Running,
        ResourceProfile.None,
        serviceHealth: null,
        createdAtUtc,
        startedAtUtc,
        stoppedAtUtc: null,
        exitCode: null,
        exitReason: null);
}
