using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Processes.Accounting;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0125: the limit and descriptor tables of the process manager, pam_limits and the enforcer.</summary>
public sealed partial class InMemoryProcessManagerTests
{
    [Fact]
    public void A_root_process_gets_the_kernel_defaults_and_a_child_inherits_its_parent_s_limits()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        Assert.True(fixture.Manager.ResourceLimits.TryGet(parent.Pid, out ProcessResourceLimits defaults));
        Assert.Equal(ProcessResourceLimits.KernelDefaults, defaults);
        Assert.Equal(new ResourceLimit(1024, 524288), defaults.NoFile);
        Assert.Equal(new ResourceLimit(4096, 4096), defaults.NProc);
        Assert.Equal(ResourceLimit.Infinite, defaults.Cpu);

        Assert.Equal(ResourceLimitChangeResult.Applied,
            fixture.Manager.ResourceLimits.TrySet(parent.Pid, ResourceLimitKind.NoFile, new ResourceLimit(64, 128), privileged: false));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.child", parent.Pid));

        Assert.Equal(new ResourceLimit(64, 128), LimitsOf(fixture.Manager, child.Pid).NoFile);
        fixture.Manager.Kill(child.Pid);
        Assert.False(fixture.Manager.ResourceLimits.TryGet(child.Pid, out _));
    }

    [Fact]
    public void Setrlimit_rules_apply_to_the_table()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        IProcessResourceLimitTable limits = fixture.Manager.ResourceLimits;

        // Lowering is always allowed; the soft value goes back up to the hard one freely.
        Assert.Equal(ResourceLimitChangeResult.Applied, limits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(10, 600), false));
        Assert.Equal(ResourceLimitChangeResult.Applied, limits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(600, 600), false));
        // Raising the hard value needs privilege (CAP_SYS_RESOURCE).
        Assert.Equal(ResourceLimitChangeResult.PermissionDenied, limits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(600, 700), false));
        Assert.Equal(ResourceLimitChangeResult.Applied, limits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(600, 700), true));
        // soft above hard, or negative: EINVAL.
        Assert.Equal(ResourceLimitChangeResult.InvalidArgument, limits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(800, 700), true));
        Assert.Equal(ResourceLimitChangeResult.InvalidArgument, limits.TrySet(process.Pid, ResourceLimitKind.Cpu, new ResourceLimit(-1, 5), true));
        // A process that does not exist: ESRCH.
        Assert.Equal(ResourceLimitChangeResult.NoSuchProcess, limits.TrySet(ProcessId.FromInt64(99999), ResourceLimitKind.NoFile, new ResourceLimit(1, 1), true));
    }

    [Fact]
    public void Turned_off_limits_are_unlimited_and_cannot_be_changed()
    {
        Fixture fixture = new();
        InMemoryProcessManager manager = new(
            fixture.Clock, fixture.Session, fixture.EventBus, 200, environments: null, new InMemoryProcessResourceLimitTable(enabled: false));
        ProcessRecord process = manager.Start(fixture.RequestFor("com.hackeros.terminal"));

        Assert.False(manager.ResourceLimits.IsEnabled);
        Assert.Equal(ProcessResourceLimits.None, LimitsOf(manager, process.Pid));
        Assert.Equal(ResourceLimitChangeResult.PermissionDenied,
            manager.ResourceLimits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(1, 1), privileged: true));
    }

    [Fact]
    public void Starting_beyond_the_nproc_limit_fails_with_eagain_unless_exempt()
    {
        Fixture fixture = new();
        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.ResourceLimits.TrySet(shell.Pid, ResourceLimitKind.NProc, new ResourceLimit(2, 2), privileged: false);
        fixture.Manager.Start(fixture.RequestFor("com.hackeros.first", shell.Pid));

        ProcessLimitExceededException refused = Assert.Throws<ProcessLimitExceededException>(
            () => fixture.Manager.Start(fixture.RequestFor("com.hackeros.second", shell.Pid)));
        Assert.Equal("EAGAIN", refused.ErrorName);
        Assert.Equal(2, refused.Limit);

        // An elevated root (CAP_SYS_RESOURCE) and a system-scope service are not held.
        Assert.NotNull(fixture.Manager.Start(fixture.RequestFor("com.hackeros.sudo", shell.Pid) with { ResourceLimitExempt = true }));
        Assert.NotNull(fixture.Manager.Start(fixture.RequestFor("com.hackeros.svc", shell.Pid) with { SystemScope = true }));
    }

    [Fact]
    public void A_root_process_starts_with_the_limits_of_its_session()
    {
        Fixture fixture = new();
        ProcessResourceLimits session = ProcessResourceLimits.KernelDefaults.With(ResourceLimitKind.Cpu, new ResourceLimit(60, 120));

        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal") with { ResourceLimits = session });

        Assert.Equal(new ResourceLimit(60, 120), LimitsOf(fixture.Manager, process.Pid).Cpu);
    }

    [Fact]
    public void Descriptors_take_the_lowest_free_number_and_stop_at_the_nofile_limit()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        IProcessFileDescriptorTable descriptors = fixture.Manager.FileDescriptors;
        fixture.Manager.ResourceLimits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(5, 5), privileged: false);

        Assert.True(descriptors.TryOpen(process.Pid, "/home/alice/a", out int first));
        Assert.True(descriptors.TryOpen(process.Pid, "/home/alice/b", out int second));
        Assert.False(descriptors.TryOpen(process.Pid, "/home/alice/c", out _));
        Assert.Equal((3, 4), (first, second));

        descriptors.Close(process.Pid, first);
        Assert.True(descriptors.TryOpen(process.Pid, "/home/alice/c", out int reused));
        Assert.Equal(3, reused);
        Assert.Equal(
            ["0 /dev/tty", "1 /dev/tty", "2 /dev/tty", "3 /home/alice/c", "4 /home/alice/b"],
            descriptors.GetOpen(process.Pid).Select(static open => $"{open.Number} {open.Target}"));

        // exit(2) closes them all.
        fixture.Manager.Kill(process.Pid);
        Assert.Empty(descriptors.GetOpen(process.Pid));
    }

    [Fact]
    public async Task A_read_holds_a_descriptor_until_its_handle_is_disposed()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.ResourceLimits.TrySet(process.Pid, ResourceLimitKind.NoFile, new ResourceLimit(4, 4), privileged: false);
        AppFileSystemGateway gateway = new(fixture.FileSystem, fixture.Clock, RootContext(process.Pid), ["system"])
        {
            Descriptors = fixture.Manager.FileDescriptors
        };

        FileSystemResult<FileSystemContentReadHandle> open = await gateway.ReadAsync(new FileSystemReadRequest(ResourceLimitsConfiguration.LimitsFilePath));
        Assert.True(open.Succeeded);
        FileSystemResult<FileSystemContentReadHandle> second = await gateway.ReadAsync(new FileSystemReadRequest(ResourceLimitsConfiguration.LimitsFilePath));
        Assert.Equal(FileSystemErrorCode.TooManyOpenFiles, second.Error?.Code);
        FileSystemResult<IDisposable> write = await gateway.OpenDescriptorAsync(ResourceLimitsConfiguration.LimitsFilePath);
        Assert.Equal(FileSystemErrorCode.TooManyOpenFiles, write.Error?.Code);

        await open.Value!.DisposeAsync();
        FileSystemResult<IDisposable> again = await gateway.OpenDescriptorAsync(ResourceLimitsConfiguration.LimitsFilePath);
        Assert.True(again.Succeeded);
        again.Value!.Dispose();
        Assert.Equal(3, fixture.Manager.FileDescriptors.GetOpen(process.Pid).Count);
    }

    [Fact]
    public async Task Pam_limits_reads_limits_conf_and_its_drop_ins_and_logs_invalid_lines()
    {
        Fixture fixture = new();
        AppFileSystemGateway root = new(fixture.FileSystem, fixture.Clock, RootContext(null), ["system"]);
        // The seeded file is commented out entirely: a session gets the kernel's values.
        BoundedAuditLog audit = new(maxEntries: 20);
        LoginResourceLimitsProvider provider = new(
            fixture.FileSystem, fixture.Clock, new InMemoryProcessResourceLimitTable(),
            (_, _) => ValueTask.FromResult<IReadOnlyCollection<string>>(["users"]), audit);
        Assert.Equal(ProcessResourceLimits.KernelDefaults, await provider.GetAsync(fixture.Principal));

        Assert.Null(await SettingsTextFile.WriteAllAsync(root, ResourceLimitsConfiguration.LimitsFilePath,
            "*      soft  nofile  4096\n@users hard  nproc   200\nalice  -     cpu     2\nalice  soft  bogus   1\n"));
        Assert.Null(await SettingsTextFile.WriteAllAsync(root, VirtualPath.Parse("/etc/security/limits.d/10-alice.conf"),
            "alice soft nofile 512\n"));
        LoginResourceLimitsProvider next = new(
            fixture.FileSystem, fixture.Clock, new InMemoryProcessResourceLimitTable(),
            (_, _) => ValueTask.FromResult<IReadOnlyCollection<string>>(["users"]), audit);

        ProcessResourceLimits limits = await next.GetAsync(fixture.Principal);

        Assert.Equal(new ResourceLimit(512, 524288), limits.NoFile);
        Assert.Equal(new ResourceLimit(200, 200), limits.NProc);
        Assert.Equal(new ResourceLimit(120, 120), limits.Cpu);
        AuditEntry warning = Assert.Single(audit.Entries, static entry => entry.Actor == "pam_limits");
        Assert.Contains("unknown limit item 'bogus'", warning.Properties["message"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_enforcer_signals_at_the_soft_cpu_limit_once_and_kills_at_the_hard_one()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.busy"));
        fixture.Manager.MarkRunning(process.Pid);
        fixture.Manager.ResourceLimits.TrySet(process.Pid, ResourceLimitKind.Cpu, new ResourceLimit(1, 3), privileged: false);
        FakeCpu cpu = new() { Seconds = 2 };
        using ResourceLimitEnforcer enforcer = new(fixture.Clock, () => fixture.Manager, fixture.Manager.ResourceLimits, cpu, NullMemoryAccountant.Instance);

        Assert.Equal([(process.Pid, ResourceLimitAction.CpuSignal)], enforcer.CheckNow());
        // SIGXCPU is a stop request (ADR 0069): the process is stopping, not yet gone.
        Assert.True(fixture.Manager.TryGetActive(process.Pid, out ProcessRecord stopping));
        Assert.Equal(ProcessState.Stopping, stopping.State);
        Assert.Empty(enforcer.CheckNow());

        cpu.Seconds = 3;
        Assert.Equal([(process.Pid, ResourceLimitAction.CpuKill)], enforcer.CheckNow());
        Assert.Equal(ProcessExitReason.CpuLimitExceeded, fixture.Manager.GetHistory().Single(p => p.Pid == process.Pid).ExitReason);
    }

    [Fact]
    public void The_enforcer_does_nothing_while_cpu_and_memory_are_not_measured()
    {
        Fixture fixture = new();
        using ResourceLimitEnforcer enforcer = new(
            fixture.Clock, () => fixture.Manager, fixture.Manager.ResourceLimits, NullCpuTimeAccountant.Instance, NullMemoryAccountant.Instance);

        Assert.False(enforcer.IsEffective);
        enforcer.Start();
        Assert.Empty(enforcer.CheckNow());
    }

    [Fact]
    public void A_process_ended_by_sigxcpu_records_the_cpu_limit_as_its_reason()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.busy"));
        fixture.Manager.MarkRunning(process.Pid);
        _ = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.CpuLimitExceeded);

        ProcessRecord ended = fixture.Manager.Complete(process.Pid, InMemoryProcessManager.CpuLimitExitCode);

        Assert.Equal(ProcessExitReason.CpuLimitExceeded, ended.ExitReason);
        Assert.Equal(152, ended.ExitCode);
    }

    private static ProcessResourceLimits LimitsOf(InMemoryProcessManager manager, ProcessId pid) =>
        manager.ResourceLimits.TryGet(pid, out ProcessResourceLimits limits) ? limits : ProcessResourceLimits.None;

    private static AppOperationContext RootContext(ProcessId? pid) => new()
    {
        AppId = "org.hackeros.kernel",
        UserId = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true,
        ProcessId = pid?.Value
    };

    private sealed class FakeCpu : ICpuTimeAccountant
    {
        public double Seconds { get; set; }

        public bool IsMeasuring => true;

        public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
        {
            coreEquivalents = 1;
            return true;
        }

        public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
        {
            times = new ProcessCpuTimes(TimeSpan.FromSeconds(Seconds), TimeSpan.Zero, 0, TimeSpan.Zero, true);
            return true;
        }

        public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
        {
            times = default;
            return false;
        }
    }
}
