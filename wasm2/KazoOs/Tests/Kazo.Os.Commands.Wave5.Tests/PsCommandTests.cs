using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Ps;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.FileSystem.Virtual;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary>
/// Exercises <c>ps</c> against the real <c>/proc</c> provider mounted in the real filesystem
/// service (ADR 0046), so the command and the documented <c>/proc</c> format stay in step.
/// </summary>
public sealed class PsCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionId CurrentSession = SessionId.FromGuid(Guid.NewGuid());
    private static readonly SessionId OtherSession = SessionId.FromGuid(Guid.NewGuid());

    [Fact]
    public async Task ExecuteAsync_ListsRealProcesses_WithoutHardcodedRows()
    {
        ProcessTable processes = new(ProcessRecordFactory.Running(
            7, "org.hackeros.text-editor", CurrentSession, Now.AddMinutes(-5), Now.AddMinutes(-5)));
        TerminalExecutionContext context = CreateContext(processes);

        int exitCode = await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp()))
            .ExecuteAsync(context, CancellationToken.None);

        string output = context.StandardOutput.ToString()!;
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("init", output);
        Assert.DoesNotContain("hackeros-shell", output);
        Assert.Contains("org.hackeros.text-editor", output);
        Assert.Contains("00:05:00", output);
        Assert.Contains("    7 R ", output);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToCurrentSession_UnlessAllUsersRequested()
    {
        ProcessTable processes = new(
            ProcessRecordFactory.Running(1, "own-session-app", CurrentSession, Now, Now),
            ProcessRecordFactory.Running(2, "other-session-app", OtherSession, Now, Now));

        TerminalExecutionContext defaultContext = CreateContext(processes);
        int defaultExitCode = await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp()))
            .ExecuteAsync(defaultContext, CancellationToken.None);
        string defaultOutput = defaultContext.StandardOutput.ToString()!;
        Assert.Equal(0, defaultExitCode);
        Assert.Contains("own-session-app", defaultOutput);
        Assert.DoesNotContain("other-session-app", defaultOutput);

        TerminalExecutionContext allContext = CreateContext(processes, "-a");
        await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp())).ExecuteAsync(allContext, CancellationToken.None);
        string allOutput = allContext.StandardOutput.ToString()!;
        Assert.Contains("own-session-app", allOutput);
        Assert.Contains("other-session-app", allOutput);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutProcessList_SeesOnlyItsOwnProcess()
    {
        ProcessTable processes = new(
            ProcessRecordFactory.Running(1, "org.hackeros.commands.ps", CurrentSession, Now, Now),
            ProcessRecordFactory.Running(2, "someone-else", CurrentSession, Now, Now));
        TerminalExecutionContext context = CreateContext(
            processes, [AppCapabilities.FileSystemSystemRead], "-a");

        await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp())).ExecuteAsync(context, CancellationToken.None);

        string output = context.StandardOutput.ToString()!;
        Assert.Contains("org.hackeros.commands.ps", output);
        Assert.DoesNotContain("someone-else", output);
    }

    [Fact]
    public async Task ExecuteAsync_UserFormat_PrintsTheOwnerFromProcStatus()
    {
        LocalUserId owner = LocalUserId.FromGuid(Guid.NewGuid());
        ProcessTable processes = new(
            ProcessRecordFactory.Running(3, "org.hackeros.calculator", CurrentSession, Now, Now, owner));
        TerminalExecutionContext context = CreateContext(processes, "-u");

        await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp())).ExecuteAsync(context, CancellationToken.None);

        string output = context.StandardOutput.ToString()!;
        Assert.StartsWith("USER", output);
        Assert.Contains(owner.ToString(), output);
    }

    [Fact]
    public async Task ExecuteAsync_LongFormat_ShowsPriorityAndNiceness()
    {
        ProcessRecord process = ProcessRecordFactory.Running(9, "org.hackeros.commands.sleep", CurrentSession, Now, Now);
        ProcessTable processes = new(process);
        processes.Nice[process.Pid] = 5;
        TerminalExecutionContext context = CreateContext(processes, "-l");

        int exitCode = await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp())).ExecuteAsync(context, CancellationToken.None);

        string[] lines = context.StandardOutput.ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(0, exitCode);
        string[] header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] row = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["F", "S", "UID", "PID", "PPID", "C", "PRI", "NI", "TIME", "CMD"], header);
        Assert.Equal("85", row[Array.IndexOf(header, "PRI")]); // 80 + nice, like procps
        Assert.Equal("5", row[Array.IndexOf(header, "NI")]);
    }

    [Fact]
    public async Task ExecuteAsync_OutputFormat_PrintsTheRequestedColumns()
    {
        ProcessRecord process = ProcessRecordFactory.Running(9, "org.hackeros.commands.sleep", CurrentSession, Now, Now);
        ProcessTable processes = new(process);
        processes.Nice[process.Pid] = -4;
        TerminalExecutionContext context = CreateContext(processes, "-o", "pid,ni,comm");

        await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp())).ExecuteAsync(context, CancellationToken.None);

        string[] lines = context.StandardOutput.ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["PID", "NI", "COMMAND"], lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(["9", "-4"], lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[..2]);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOutputField_Fails()
    {
        TerminalExecutionContext context = CreateContext(new ProcessTable(), "-o", "pid,bogus");

        int exitCode = await new PsCommand(AppDefinitionMapper.ToManifest(new PsApp())).ExecuteAsync(context, CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("bogus", context.StandardError.ToString());
    }

    private static TerminalExecutionContext CreateContext(ProcessTable processes, params string[] arguments) =>
        CreateContext(processes, AppDefinitionMapper.ToManifest(new PsApp()).Capabilities, arguments);

    /// <summary>
    /// Mounts the real <c>/proc</c> provider over an in-memory root and hands <c>ps</c> the same
    /// <see cref="AppFileSystemGateway"/> the platform would, running as PID 1.
    /// </summary>
    private static TerminalExecutionContext CreateContext(
        ProcessTable processes, IEnumerable<string> capabilities, params string[] arguments)
    {
        ManualSimulationClock clock = new(Now, TimeSpan.FromMilliseconds(100));
        FileSystemMountRouter router = new(new InMemoryFileSystemRepository(),
        [
            new FileSystemMount(ProcFileSystemProvider.DefaultMountPath, new ProcFileSystemProvider(
                () => processes,
                new DeterministicResourceSimulator(clock, new SeededSimulationRandom(1), VirtualHardwareProfile.Default),
                VirtualHardwareProfile.Default,
                clock))
        ]);
        FileSystemService service = new(
            router,
            new FileSystemPathResolver(router),
            new FileSystemAuthorizer(),
            new InMemoryTopicMessageBus(new CapabilityGrantRepository()));
        AppOperationContext operation = new()
        {
            AppId = AppDefinitionMapper.ToManifest(new PsApp()).Id,
            UserId = "user",
            UserAuthority = AppAuthority.User,
            GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
            ProcessId = 1
        };

        return new TerminalExecutionContext(
            new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new PsApp()),
                fileSystem: new AppFileSystemGateway(service, clock, operation, ["users"]),
                clock: new FakeAppClockGateway(Now),
                sessionId: CurrentSession),
            arguments,
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string>());
    }

    /// <summary>Process table double: only the read side <c>/proc</c> uses is implemented.</summary>
    private sealed class ProcessTable(params ProcessRecord[] active) : IProcessManager
    {
        /// <summary>Niceness per process (ADR 0127); absent means 0.</summary>
        public Dictionary<ProcessId, int> Nice { get; } = [];

        public int GetNice(ProcessId pid) => Nice.GetValueOrDefault(pid);

        public IReadOnlyList<ProcessRecord> GetActiveProcesses() => active;

        public IReadOnlyList<ProcessRecord> GetHistory() => [];

        public bool TryGetActive(ProcessId pid, out ProcessRecord process)
        {
            process = active.FirstOrDefault(candidate => candidate.Pid == pid)!;
            return process is not null;
        }

        public ProcessRecord Start(ProcessStartRequest request) => throw new NotSupportedException();

        public ProcessRecord MarkRunning(ProcessId pid) => throw new NotSupportedException();

        public ProcessRecord Complete(ProcessId pid, int exitCode) => throw new NotSupportedException();

        public ProcessRecord Fault(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Fault) =>
            throw new NotSupportedException();

        public Task<ProcessRecord> StopAsync(
            ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed) =>
            throw new NotSupportedException();

        public bool TryGetSingleton(string appId, out ProcessRecord process) => throw new NotSupportedException();

        public CancellationToken GetCancellationToken(ProcessId pid) => throw new NotSupportedException();
    }
}
