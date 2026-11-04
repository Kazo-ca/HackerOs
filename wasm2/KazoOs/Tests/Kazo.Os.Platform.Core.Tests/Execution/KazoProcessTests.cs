using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.ServiceProcess;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// ADR 0056 sections 4 and 5: <see cref="KazoProcess"/> and <see cref="KazoServiceController"/> read
/// the real <c>/proc</c> provider through the app's own file gateway, exactly like <c>ps</c>.
/// </summary>
public sealed class KazoProcessTests
{
    private const long OwnPid = 2;
    private static readonly LocalUserId Bob = LocalUserId.FromGuid(new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc"));

    [Fact]
    public async Task GetProcessesAsync_reads_status_cmdline_cwd_io_and_the_start_time_from_proc()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        ProcessRecord editor = Process(42, "org.kazoos.text-editor", parentPid: OwnPid);
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), editor]);
        fixture.Resources.ProcessSamples[editor.Pid] = [new ProcessResourceSample(editor.Pid, 1, BootUtc, 0.25, 3 * 1024 * 1024, 0, 0)];
        fixture.Environments.Create(
            new ProcessStartRequest(
                OwnProcessId, "org.kazoos.text-editor", AppInstanceId.FromGuid(Guid.NewGuid()), AppKind.Window,
                Alice, Session, ResourceProfile.None,
                ["text-editor", "/home/alice/my notes.md"],
                VirtualPath.Parse("/home/alice/Documents"),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = "/home/alice" }),
            editor.Pid);
        fixture.ProcessIo.RecordRead(editor.Pid, 4096, fromStorage: true);
        fixture.ProcessIo.RecordWrite(editor.Pid, 100, toStorage: false);
        using IDisposable scope = Enter(fixture, AppCapabilities.ProcessList);

        KazoProcess[] processes = await KazoProcess.GetProcessesAsync();

        Assert.Equal([OwnPid, 42], processes.Select(static process => process.Id));
        KazoProcess process = processes[1];
        Assert.Equal("text-editor", process.ProcessName);
        Assert.Equal("org.kazoos.text-editor", process.AppId);
        Assert.Equal(OwnPid, process.ParentId);
        Assert.Equal(AppKind.Window, process.Kind);
        Assert.Equal(KazoProcessState.Running, process.State);
        Assert.Equal('R', process.StateCode);
        Assert.Equal("alice", process.UserName);
        Assert.Equal(Alice.ToString(), process.UserId);
        Assert.Equal(Session.ToString(), process.SessionId);
        Assert.Equal(BootUtc.AddSeconds(42), process.StartTime);
        Assert.Equal(3L * 1024 * 1024, process.WorkingSet64);
        Assert.Equal(3L * 1024 * 1024, process.PrivateMemorySize64); // simulated: no RssAnon, equals VmRSS
        Assert.Equal(25.0, process.CpuUsage, 3);
        Assert.Equal(["text-editor", "/home/alice/my notes.md"], process.Arguments);
        Assert.Equal("text-editor \"/home/alice/my notes.md\"", process.CommandLine);
        Assert.Equal("/home/alice/Documents", process.WorkingDirectory);
        Assert.Equal(new KazoProcessIoCounters(4096, 100, 1, 1, 4096, 0, 0), process.IoCounters);
        Assert.Equal(4096, process.ReadBytes);
        Assert.Equal("text-editor", process.Status["Name"]);
        Assert.False(process.HasExited);
        Assert.Equal(["HOME"], (await process.GetEnvironmentVariablesAsync())!.Keys);
        Assert.Equal([process], processes[0].GetChildren(processes));
    }

    [Fact]
    public async Task Processor_times_and_responding_come_from_proc_stat_and_status_when_measured()
    {
        // ADR 0067: utime/stime of /proc/<pid>/stat behind the Process-like properties.
        VirtualFileSystemFixture fixture = await CreateSeededAsync(new MeasuredTimes(
            new ProcessCpuTimes(TimeSpan.FromMilliseconds(1230), TimeSpan.FromMilliseconds(450), 2, TimeSpan.FromSeconds(7), false, 9)));
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), Process(42, "org.kazoos.text-editor")]);
        using IDisposable scope = Enter(fixture, AppCapabilities.ProcessList);

        KazoProcess process = await KazoProcess.GetProcessByIdAsync(42);

        Assert.Equal(TimeSpan.FromMilliseconds(1230), process.UserProcessorTime);
        Assert.Equal(TimeSpan.FromMilliseconds(450), process.PrivilegedProcessorTime);
        Assert.Equal(TimeSpan.FromMilliseconds(1680), process.TotalProcessorTime);
        Assert.False(process.Responding);
        Assert.Equal("measured", process.Status["CpuSource"]);
    }

    [Fact]
    public async Task Simulated_processor_time_is_rebuilt_from_the_simulator_and_the_process_responds()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        ProcessRecord editor = Process(42, "org.kazoos.text-editor");
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), editor]);
        fixture.Resources.ProcessSamples[editor.Pid] = [new ProcessResourceSample(editor.Pid, 1, BootUtc, 0.25, 0, 0, 0)];
        using IDisposable scope = Enter(fixture, AppCapabilities.ProcessList);

        KazoProcess process = await KazoProcess.GetProcessByIdAsync(42);

        Assert.Equal(TimeSpan.FromMilliseconds(250), process.UserProcessorTime);
        Assert.Equal(TimeSpan.Zero, process.PrivilegedProcessorTime);
        Assert.True(process.Responding);
    }

    [Theory]
    [InlineData("42 (code-editor) R 1 42 42 0 -1 0 0 0 0 0 150 25 0 0 20 0 1 0", 1500, 250)]
    [InlineData("7 (my (odd) app) S 1 7 7 0 -1 0 0 0 0 0 3 1 0 0", 30, 10)]
    [InlineData("7 (short) S 1", 0, 0)]
    public void ParseStatTimes_counts_fields_after_the_last_parenthesis(string line, int userMs, int systemMs)
    {
        (TimeSpan user, TimeSpan system) = KazoProcess.ParseStatTimes(line);

        Assert.Equal(TimeSpan.FromMilliseconds(userMs), user);
        Assert.Equal(TimeSpan.FromMilliseconds(systemMs), system);
    }

    [Fact]
    public async Task Measured_memory_gives_WorkingSet64_from_VmRSS_and_PrivateMemorySize64_from_RssAnon()
    {
        // ADR 0068, section 2, modeled on System.Diagnostics.Process.
        VirtualFileSystemFixture fixture = await CreateSeededAsync(memoryAccountant: new LayeredMemoryAccountant(
            new ProcessMemoryUsage(ImageBytes: 40 * 1024, FileBytes: 800 * 1024, AnonymousBytes: 2048 * 1024, KernelBytes: 512 * 1024, AllocatedBytes: 10)));
        fixture.Processes.Active.Add(Process(OwnPid, "org.kazoos.system-monitor"));
        using IDisposable scope = Enter(fixture);

        KazoProcess own = await KazoProcess.GetCurrentProcessAsync();

        Assert.Equal((800L + 2048 + 512) * 1024, own.WorkingSet64);
        Assert.Equal(2048L * 1024, own.PrivateMemorySize64);
        Assert.Equal("measured", own.Status["MemSource"]);
    }

    private sealed class LayeredMemoryAccountant(ProcessMemoryUsage usage) : IMemoryAccountant
    {
        public bool IsMeasuring => true;

        public bool TryGetProcessMemory(ProcessRecord process, IReadOnlyList<ProcessRecord> active, out ProcessMemoryUsage result)
        {
            result = usage;
            return true;
        }

        public bool TryGetSystemMemory(IReadOnlyList<ProcessRecord> active, out SystemMemoryUsage result)
        {
            result = new SystemMemoryUsage(usage.AnonymousBytes, usage.FileBytes, usage.KernelBytes, 1);
            return true;
        }
    }

    [Fact]
    public async Task Without_process_list_only_the_own_process_is_visible_like_ps()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), Process(42, "org.kazoos.text-editor")]);
        using IDisposable scope = Enter(fixture);

        KazoProcess own = Assert.Single(await KazoProcess.GetProcessesAsync());

        Assert.Equal(OwnPid, own.Id);
        Assert.Equal(OwnPid, (await KazoProcess.GetCurrentProcessAsync()).Id);
        ArgumentException missing = await Assert.ThrowsAsync<ArgumentException>(() => KazoProcess.GetProcessByIdAsync(42));
        Assert.Contains("42", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_users_io_and_environment_are_private_and_read_as_null()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), Process(77, "org.kazoos.sync", userId: Bob)]);
        using IDisposable scope = Enter(fixture, AppCapabilities.ProcessList);

        KazoProcess other = await KazoProcess.GetProcessByIdAsync(77);

        Assert.Null(other.IoCounters);
        Assert.Equal(0, other.ReadBytes);
        Assert.Null(await other.GetEnvironmentVariablesAsync());
        // The OS can only name the caller's own user; another uid reads as itself, like ps without /etc/passwd.
        Assert.Equal(Bob.ToString(), other.UserName);
    }

    [Fact]
    public async Task RefreshAsync_marks_an_exited_process()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        ProcessRecord editor = Process(42, "org.kazoos.text-editor");
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), editor]);
        using IDisposable scope = Enter(fixture, AppCapabilities.ProcessList);
        KazoProcess process = await KazoProcess.GetProcessByIdAsync(42);

        fixture.Processes.Active.Remove(editor);
        await process.RefreshAsync();

        Assert.True(process.HasExited);
        Assert.Equal("text-editor", process.ProcessName);
    }

    [Fact]
    public async Task KillAsync_goes_through_the_process_gateway_and_maps_a_denial_and_an_exit()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.AddRange(
            [Process(OwnPid, "org.kazoos.system-monitor"), Process(42, "org.kazoos.text-editor"), Process(43, "org.kazoos.calculator")]);
        RecordingProcessGateway gateway = new() { Denied = { 43 } };
        using IDisposable scope = Enter(fixture, gateway, AppCapabilities.ProcessList);
        KazoProcess[] processes = await KazoProcess.GetProcessesAsync();

        await processes[1].KillAsync();
        KazoUnauthorizedAccessException denied = await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => processes[2].KillAsync());
        gateway.Gone.Add(42);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processes[1].KillAsync());

        Assert.Equal([(42L, true)], gateway.Calls);
        Assert.True(processes[1].HasExited);
        Assert.Equal(AppCapabilities.ProcessManage, denied.RequiredCapability);
    }

    [Fact]
    public async Task CloseMainWindowAsync_asks_politely_and_reports_whether_the_process_had_to_be_forced()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.AddRange([Process(OwnPid, "org.kazoos.system-monitor"), Process(42, "org.kazoos.text-editor")]);
        RecordingProcessGateway gateway = new();
        using IDisposable scope = Enter(fixture, gateway, AppCapabilities.ProcessList);
        KazoProcess editor = await KazoProcess.GetProcessByIdAsync(42);

        Assert.True(await editor.CloseMainWindowAsync(TimeSpan.FromSeconds(2)));
        gateway.StopReason = ProcessExitReason.Timeout;
        Assert.False(await editor.CloseMainWindowAsync());

        Assert.Equal([(42L, false), (42L, false)], gateway.Calls);
        Assert.Equal([TimeSpan.FromSeconds(2), KazoProcess.DefaultCloseTimeout], gateway.Timeouts);
        Assert.Equal([ProcessExitReason.CloseRequested, ProcessExitReason.CloseRequested], gateway.Reasons);
    }

    [Fact]
    public async Task TerminateAsync_sends_sigterm_with_a_five_second_grace_and_reports_a_forced_end()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.AddRange(
            [Process(OwnPid, "org.kazoos.system-monitor"), Process(42, "org.kazoos.text-editor"), Process(43, "org.kazoos.calculator")]);
        RecordingProcessGateway gateway = new() { Denied = { 43 } };
        using IDisposable scope = Enter(fixture, gateway, AppCapabilities.ProcessList);
        KazoProcess editor = await KazoProcess.GetProcessByIdAsync(42);
        KazoProcess calculator = await KazoProcess.GetProcessByIdAsync(43);

        gateway.StopReason = ProcessExitReason.Completed;
        Assert.True(await editor.TerminateAsync());
        gateway.StopReason = ProcessExitReason.Timeout;
        Assert.False(await editor.TerminateAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => calculator.TerminateAsync());

        Assert.Equal([(42L, false), (42L, false)], gateway.Calls);
        Assert.Equal([KazoProcess.DefaultTerminateTimeout, TimeSpan.FromSeconds(1)], gateway.Timeouts);
        Assert.Equal([ProcessExitReason.Terminated, ProcessExitReason.Terminated], gateway.Reasons);
        Assert.Equal(TimeSpan.FromSeconds(5), KazoProcess.DefaultTerminateTimeout);
        Assert.True(editor.HasExited);
    }

    [Fact]
    public async Task Services_come_from_the_installed_apps_with_their_status_from_proc()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.AddRange(
            [Process(OwnPid, "org.kazoos.system-monitor"), Process(60, "org.kazoos.sync", kind: AppKind.Service)]);
        FakeServices services = new() { Modes = { ["org.kazoos.sync"] = ServiceStartMode.Automatic }, DeniedReads = { "org.kazoos.indexer" } };
        FakeCatalog catalog = new(
            App("org.kazoos.sync", "Sync", AppKind.Service),
            App("org.kazoos.indexer", "Indexer", AppKind.Service),
            App("org.kazoos.text-editor", "Text Editor", AppKind.Window));
        using IDisposable scope = Enter(fixture, new RecordingProcessGateway(), catalog, services, AppCapabilities.ProcessList);

        KazoServiceController[] list = await KazoServiceController.GetServicesAsync();

        Assert.Equal(["org.kazoos.indexer", "org.kazoos.sync"], list.Select(static service => service.ServiceName));
        Assert.Equal(KazoServiceControllerStatus.Stopped, list[0].Status);
        Assert.Null(list[0].StartType);
        Assert.Equal(KazoServiceControllerStatus.Running, list[1].Status);
        Assert.Equal(60, list[1].ProcessId);
        Assert.Equal(ServiceStartMode.Automatic, list[1].StartType);

        await list[1].SetStartTypeAsync(ServiceStartMode.Disabled);
        Assert.Equal(ServiceStartMode.Disabled, list[1].StartType);

        services.NextOutcome = ServiceControlOutcome.ServiceDisabled;
        InvalidOperationException disabled = await Assert.ThrowsAsync<InvalidOperationException>(() => list[0].StartAsync());
        Assert.Contains("disabled", disabled.Message, StringComparison.Ordinal);

        services.DenyActions = true;
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => list[1].StopAsync());
        Assert.Equal(["set:org.kazoos.sync:Disabled", "start:org.kazoos.indexer", "stop:org.kazoos.sync"], services.Calls);
    }

    [Fact]
    public async Task A_service_reads_its_state_from_the_service_manager_and_can_be_reset()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        DateTimeOffset since = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        FakeServices services = new()
        {
            Statuses =
            {
                ["org.kazoos.sync"] = new ServiceStatusSnapshot(
                    ServiceActiveState.Failed, ServiceRunResult.Exception, ServiceHealth.Unknown, null, since, null, null),
                ["org.kazoos.indexer"] = new ServiceStatusSnapshot(
                    ServiceActiveState.Active, ServiceRunResult.Success, ServiceHealth.Degraded, "cache cold", since, 61, null)
            }
        };
        FakeCatalog catalog = new(App("org.kazoos.sync", "Sync", AppKind.Service), App("org.kazoos.indexer", "Indexer", AppKind.Service));
        using IDisposable scope = Enter(fixture, new RecordingProcessGateway(), catalog, services, AppCapabilities.ProcessList);

        KazoServiceController[] list = await KazoServiceController.GetServicesAsync();
        KazoServiceController indexer = list[0];
        KazoServiceController sync = list[1];

        // ADR 0097: the service manager's state wins over /proc, which shows neither failed nor health.
        Assert.Equal(KazoServiceActiveState.Active, indexer.ActiveState);
        Assert.Equal(KazoServiceHealth.Degraded, indexer.Health);
        Assert.Equal("cache cold", indexer.StatusText);
        Assert.Equal(61, indexer.ProcessId);
        Assert.Equal(KazoServiceActiveState.Failed, sync.ActiveState);
        Assert.Equal(KazoServiceResult.Exception, sync.Result);
        Assert.Equal(KazoServiceControllerStatus.Stopped, sync.Status);
        Assert.Equal(since, sync.ActiveStateChangedAt);
        Assert.Equal("exception", KazoServiceStates.ToSystemdName(sync.Result));

        await sync.ResetFailedAsync();
        Assert.Equal(KazoServiceActiveState.Inactive, sync.ActiveState);
        Assert.Contains("reset:org.kazoos.sync", services.Calls);
    }

    private static readonly ProcessId OwnProcessId = ProcessId.FromInt64(OwnPid);

    private static IDisposable Enter(VirtualFileSystemFixture fixture, params string[] capabilities) =>
        Enter(fixture, new RecordingProcessGateway(), capabilities);

    private static IDisposable Enter(VirtualFileSystemFixture fixture, IAppProcessGateway processes, params string[] capabilities) =>
        Enter(fixture, processes, new FakeCatalog(), new FakeServices(), capabilities);

    private static IDisposable Enter(
        VirtualFileSystemFixture fixture,
        IAppProcessGateway processes,
        IAppCatalogGateway catalog,
        IAppServiceControlGateway services,
        params string[] capabilities)
    {
        string[] granted = [AppCapabilities.FileSystemSystemRead, .. capabilities];
        AppOperationContext operation = new()
        {
            AppId = "org.kazoos.system-monitor",
            UserId = Alice.ToString(),
            UserName = "alice",
            UserAuthority = AppAuthority.User,
            GrantedCapabilities = new HashSet<string>(granted, StringComparer.Ordinal),
            ProcessId = OwnPid
        };
        AppFileSystemGateway fileSystem = new(fixture.Service, fixture.Clock, operation, ["users"]);
        return KazoExecutionContext.Enter(new TaskManagerContext(fileSystem, processes, catalog, services, granted));
    }

    private static KazoAppInfo App(string id, string name, AppKind kind) =>
        new(id, name, $"{name} test app", kind, "system", null, "1.0.0", "org.tests", true, false, []);

    /// <summary>The gateways a task manager uses: files (real), processes, catalog and services (fakes).</summary>
    private sealed class TaskManagerContext(
        IAppFileSystemGateway fileSystem,
        IAppProcessGateway processes,
        IAppCatalogGateway catalog,
        IAppServiceControlGateway services,
        IReadOnlyCollection<string> granted) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = new()
        {
            Id = "org.kazoos.system-monitor",
            Name = "System Monitor",
            Version = "1.0.0",
            PublisherId = "org.tests",
            Description = "KazoProcess test application.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "TaskManagerApp"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };

        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => Alice.ToString();
        public string UserLoginName => "alice";
        public string HomeDirectory => "/home/alice";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(granted, StringComparer.Ordinal);
        public SessionId SessionId => Session;
        public ProcessId ProcessId => OwnProcessId;
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotSupportedException();
        public IAppFileSystemGateway FileSystem { get; } = fileSystem;
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppLoggingGateway Logging => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes { get; } = processes;
        public IAppCatalogGateway Catalog { get; } = catalog;
        public IAppServiceControlGateway Services { get; } = services;
    }

    /// <summary>Records stop and kill calls; denies or forgets the PIDs a test lists.</summary>
    private sealed class RecordingProcessGateway : IAppProcessGateway
    {
        public HashSet<long> Denied { get; } = [];
        public HashSet<long> Gone { get; } = [];
        public List<(long Pid, bool Forced)> Calls { get; } = [];
        public List<TimeSpan> Timeouts { get; } = [];
        public List<ProcessExitReason> Reasons { get; } = [];
        public ProcessExitReason StopReason { get; set; } = ProcessExitReason.CloseRequested;

        public ProcessRecord OwnProcess => throw new NotSupportedException();

        public ProcessRecord StartChild(string appId, AppInstanceId appInstanceId, AppKind kind, ResourceProfile resourceProfile) =>
            throw new NotSupportedException();

        public IReadOnlyList<ProcessRecord> ListProcesses() => throw new NotSupportedException();

        public async Task<ProcessRecord> StopAsync(
            ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default)
        {
            // Like AppProcessGateway, the capability check happens inside the task.
            await Task.Yield();
            Check(pid);
            Calls.Add((pid.Value, false));
            Timeouts.Add(timeout);
            Reasons.Add(reason);
            ProcessRecord stopped = Process(pid.Value, "org.tests.stopped", ProcessState.Stopped);
            return new ProcessRecord(
                stopped.Pid, stopped.ParentPid, stopped.AppId, stopped.AppInstanceId, stopped.Kind, stopped.UserId, stopped.SessionId,
                ProcessState.Stopped, stopped.ResourceProfile, null, stopped.CreatedAtUtc, stopped.StartedAtUtc, stopped.StoppedAtUtc,
                0, StopReason);
        }

        public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed)
        {
            Check(pid);
            Calls.Add((pid.Value, true));
            return Process(pid.Value, "org.tests.killed", ProcessState.Stopped);
        }

        private void Check(ProcessId pid)
        {
            if (Denied.Contains(pid.Value))
            {
                throw new AppGatewayAccessDeniedException(AppCapabilities.ProcessManage, CapabilityPolicyEvaluation.DenyMissing(1));
            }

            if (Gone.Contains(pid.Value))
            {
                throw new InvalidOperationException($"Process {pid.Value} is not active.");
            }
        }
    }

    private sealed class FakeCatalog(params KazoAppInfo[] apps) : IAppCatalogGateway
    {
        public event EventHandler? Changed { add { } remove { } }

        public ValueTask<IReadOnlyList<KazoAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoAppInfo>>(apps);

        public ValueTask<IReadOnlyList<KazoAppInfo>> GetHandlersAsync(
            VirtualPath path, string? action = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeServices : IAppServiceControlGateway
    {
        public Dictionary<string, ServiceStartMode> Modes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DeniedReads { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public ServiceControlOutcome NextOutcome { get; set; } = ServiceControlOutcome.Succeeded;
        public bool DenyActions { get; set; }

        /// <summary>States the service manager reports (ADR 0097); a service not listed falls back to /proc.</summary>
        public Dictionary<string, ServiceStatusSnapshot> Statuses { get; } = new(StringComparer.Ordinal);

        public ValueTask<ServiceStatusSnapshot> GetStatusAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            Statuses.TryGetValue(serviceAppId, out ServiceStatusSnapshot? status)
                ? ValueTask.FromResult(status)
                : throw new NotSupportedException();

        public ValueTask<ServiceControlResult> ResetFailedAsync(string serviceAppId, CancellationToken cancellationToken = default)
        {
            Statuses[serviceAppId] = ServiceStatusSnapshot.NeverStarted;
            return Act($"reset:{serviceAppId}");
        }

        public ValueTask<ServiceControlResult> StartAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            Act($"start:{serviceAppId}");

        public ValueTask<ServiceControlResult> StopAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            Act($"stop:{serviceAppId}");

        public ValueTask<ServiceStartMode> GetStartModeAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            DeniedReads.Contains(serviceAppId)
                ? throw new AppGatewayAccessDeniedException(AppCapabilities.ServicesManage, CapabilityPolicyEvaluation.DenyMissing(1))
                : ValueTask.FromResult(Modes.GetValueOrDefault(serviceAppId, ServiceStartMode.Manual));

        public ValueTask<ServiceControlResult> SetStartModeAsync(
            string serviceAppId, ServiceStartMode mode, CancellationToken cancellationToken = default)
        {
            Modes[serviceAppId] = mode;
            return Act($"set:{serviceAppId}:{mode}");
        }

        private ValueTask<ServiceControlResult> Act(string call)
        {
            Calls.Add(call);
            if (DenyActions)
            {
                throw new AppGatewayAccessDeniedException(AppCapabilities.ServicesManage, CapabilityPolicyEvaluation.DenyMissing(1));
            }

            ServiceControlOutcome outcome = NextOutcome;
            NextOutcome = ServiceControlOutcome.Succeeded;
            return ValueTask.FromResult(new ServiceControlResult(outcome));
        }
    }

    /// <summary>A CPU accountant that measures every process with the same times.</summary>
    private sealed class MeasuredTimes(ProcessCpuTimes times) : ICpuTimeAccountant
    {
        public bool IsMeasuring => true;

        public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
        {
            coreEquivalents = 0.1;
            return true;
        }

        public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes processTimes)
        {
            processTimes = times;
            return true;
        }

        public bool TryGetThreadTimes(out MeasuredThreadCpuTimes threadTimes)
        {
            threadTimes = default;
            return false;
        }
    }
}
