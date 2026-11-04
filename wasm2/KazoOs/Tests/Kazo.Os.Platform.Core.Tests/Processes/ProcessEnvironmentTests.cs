using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>Per-process arguments, working directory and environment (ADR 0050, section 5).</summary>
public sealed class ProcessEnvironmentTests
{
    private static readonly LocalUserId Alice = LocalUserId.FromGuid(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly SessionId Session = SessionId.FromGuid(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [Fact]
    public void A_root_process_gets_the_requested_values_or_root_and_its_app_id()
    {
        InMemoryProcessManager manager = CreateManager();

        ProcessRecord bare = manager.Start(Request("org.hackeros.terminal"));
        ProcessRecord explicitOne = manager.Start(Request(
            "org.hackeros.commands.ls", arguments: ["ls", "-la"], directory: "/home/alice",
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = "/home/alice" }));

        ProcessEnvironmentSnapshot bareState = manager.Environments.Get(bare.Pid);
        Assert.Equal(["org.hackeros.terminal"], bareState.Arguments);
        Assert.Equal("/", bareState.WorkingDirectory);
        Assert.Empty(bareState.Variables);

        ProcessEnvironmentSnapshot state = manager.Environments.Get(explicitOne.Pid);
        Assert.Equal(["ls", "-la"], state.Arguments);
        Assert.Equal("/home/alice", state.WorkingDirectory);
        Assert.Equal("/home/alice", state.Variables["HOME"]);
    }

    [Fact]
    public void A_child_copies_its_parent_at_creation_then_both_evolve_apart()
    {
        InMemoryProcessManager manager = CreateManager();
        ProcessRecord shell = manager.Start(Request(
            "org.hackeros.terminal", directory: "/home/alice",
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/bin", ["EDITOR"] = "nano" }));

        ProcessRecord child = manager.Start(Request(
            "org.hackeros.commands.env", parent: shell.Pid,
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["EDITOR"] = "vi" }));

        ProcessEnvironmentSnapshot childState = manager.Environments.Get(child.Pid);
        Assert.Equal("/home/alice", childState.WorkingDirectory);
        Assert.Equal("/bin", childState.Variables["PATH"]);
        Assert.Equal("vi", childState.Variables["EDITOR"]);

        // Later changes on one side never reach the other.
        manager.Environments.SetVariable(shell.Pid, "LATE", "1");
        manager.Environments.SetWorkingDirectory(shell.Pid, VirtualPath.Parse("/tmp"));
        manager.Environments.SetVariable(child.Pid, "PATH", null);

        Assert.False(manager.Environments.Get(child.Pid).Variables.ContainsKey("LATE"));
        Assert.Equal("/home/alice", manager.Environments.Get(child.Pid).WorkingDirectory);
        Assert.Equal("/bin", manager.Environments.Get(shell.Pid).Variables["PATH"]);
        Assert.Equal("nano", manager.Environments.Get(shell.Pid).Variables["EDITOR"]);
    }

    [Fact]
    public void Snapshots_are_copies_that_later_changes_do_not_alter()
    {
        InMemoryProcessManager manager = CreateManager();
        ProcessRecord process = manager.Start(Request("org.hackeros.terminal"));
        ProcessEnvironmentSnapshot before = manager.Environments.Get(process.Pid);

        manager.Environments.SetVariable(process.Pid, "X", "1");

        Assert.Empty(before.Variables);
        Assert.Equal("1", manager.Environments.Get(process.Pid).Variables["X"]);
    }

    [Fact]
    public void The_entry_disappears_when_the_process_ends_including_killed_descendants()
    {
        InMemoryProcessManager manager = CreateManager();
        ProcessRecord parent = manager.Start(Request("org.hackeros.terminal"));
        ProcessRecord child = manager.Start(Request("org.hackeros.commands.sleep", parent: parent.Pid));
        ProcessRecord other = manager.Start(Request("org.hackeros.commands.ls"));
        manager.MarkRunning(other.Pid);
        manager.Complete(other.Pid, 0);

        manager.Kill(parent.Pid);

        Assert.False(manager.Environments.TryGet(parent.Pid, out _));
        Assert.False(manager.Environments.TryGet(child.Pid, out _));
        Assert.False(manager.Environments.TryGet(other.Pid, out _));
        Assert.Throws<KeyNotFoundException>(() => manager.Environments.SetVariable(parent.Pid, "X", "1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("A\0B")]
    public void Invalid_names_are_rejected(string name)
    {
        InMemoryProcessManager manager = CreateManager();
        ProcessRecord process = manager.Start(Request("org.hackeros.terminal"));

        Assert.Throws<ArgumentException>(() => manager.Environments.SetVariable(process.Pid, name, "x"));
        Assert.Throws<ArgumentException>(() => manager.Start(Request(
            "org.hackeros.terminal", environment: new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "x" })));
    }

    [Fact]
    public void Values_containing_nul_are_rejected_but_empty_values_are_kept()
    {
        InMemoryProcessManager manager = CreateManager();
        ProcessRecord process = manager.Start(Request("org.hackeros.terminal"));

        Assert.Throws<ArgumentException>(() => manager.Environments.SetVariable(process.Pid, "X", "a\0b"));
        manager.Environments.SetVariable(process.Pid, "EMPTY", string.Empty);
        Assert.Equal(string.Empty, manager.Environments.Get(process.Pid).Variables["EMPTY"]);
    }

    private static InMemoryProcessManager CreateManager()
    {
        ManualSimulationClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        return new InMemoryProcessManager(clock, new StubSessionService(), new InMemoryEventBus());
    }

    private static ProcessStartRequest Request(
        string appId,
        ProcessId? parent = null,
        IReadOnlyList<string>? arguments = null,
        string? directory = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(parent, appId, AppInstanceId.FromGuid(Guid.NewGuid()), AppKind.Terminal, Alice, Session, ResourceProfile.None,
            arguments, directory is null ? null : VirtualPath.Parse(directory), environment);

    /// <summary>Active session whose cancellation sources are plain, unlinked ones.</summary>
    private sealed class StubSessionService : ISessionService
    {
        public SessionState State => SessionState.Active;
        public AuthenticatedPrincipal? CurrentPrincipal => null;

        public Task<AuthenticatedPrincipal> LoginAsync(
            LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public CancellationTokenSource CreateLinkedCancellationSource() => new();
    }
}
