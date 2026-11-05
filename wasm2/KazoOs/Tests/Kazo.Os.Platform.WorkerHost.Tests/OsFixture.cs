using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.FileSystem.Virtual;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Metrics;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Platform.Core.Network.Local;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Platform.WorkerHost.Host;
using Kazo.Os.Platform.WorkerHost.Loopback;
using Kazo.Os.Platform.Core;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Network;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// A whole headless OS for the worker tests: the real filesystem with <c>/proc</c> and <c>/dev</c>,
/// sessions, process manager, simulated network, catalog, shell interpreter, every built-in command
/// of <c>Kazo.Os.Commands</c> and a few test commands. With <c>workers</c>, commands and services run
/// in loopback Web Workers through the real protocol (ADR 0070); without, in-process as before.
/// </summary>
internal sealed class OsFixture : IAsyncDisposable
{
    public const string HostAppId = "org.kazoos.terminal";

    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] HostCapabilities =
    [
        AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite,
        AppCapabilities.AppsLaunch, AppCapabilities.ShellHost, AppCapabilities.ProcessList
    ];

    /// <summary>Gets the page's isolation as the boot probe would record it (ADR 0146); not in a browser by default.</summary>
    public Kazo.Os.Platform.Core.Isolation.CrossOriginIsolationState Isolation { get; } = new();

    private readonly LocalLoginName _alice = LocalLoginName.Parse("alice");
    private readonly CapabilityGrantRepository _grants;
    private readonly LocalSessionService _sessions;
    private int _entryId = 1;
    private int _transactionId = 1000;

    private OsFixture(
        bool workers, WorkerPoolOptions? poolOptions, int hardwareConcurrency, TimeSpan? workerStartupDelay,
        Func<Kazo.Os.Platform.WorkerHost.Worker.IWorkerAssemblyLoader>? loaderFactory, bool synchronousCalls = false,
        Func<IWebWorkerRuntime, IWebWorkerRuntime>? wrapRuntime = null)
    {
        FixedTimeProvider timeProvider = new(Now);
        InMemoryFileSystemRepository repository = new(
            () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
            () => new Guid(_transactionId++, 0, 0, new byte[8]),
            timeProvider);
        Clock = new ManualSimulationClock(Now, TimeSpan.FromSeconds(1));
        DeterministicResourceSimulator resources = new(Clock, new SeededSimulationRandom(42), VirtualHardwareProfile.Default);
        ProcFileSystemProvider proc = new(
            () => Manager!, resources, VirtualHardwareProfile.Default, Clock,
            environments: () => Manager?.Environments,
            transactionIdFactory: () => new Guid(_transactionId++, 0, 0, new byte[8]),
            metrics: new ProcMetricsSources(Workers: () => Pool, Listeners: () => Ports) { BackgroundJobs = () => BackgroundJobs });
        FileSystemMountRouter router = new(repository,
        [
            new FileSystemMount(ProcFileSystemProvider.DefaultMountPath, proc),
            new FileSystemMount(DevFileSystemProvider.DefaultMountPath, new DevFileSystemProvider(Clock))
        ]);
        _grants = new CapabilityGrantRepository(() => Now);
        InMemoryTopicMessageBus topicBus = new(_grants);
        TopicBus = topicBus;
        FileSystemService fileSystem = new(
            router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
            () => new Guid(_transactionId++, 0, 0, new byte[8]));
        FileSystem = fileSystem;
        FileSystemSeeder seeder = new(fileSystem, timeProvider);

        InMemoryLocalUserRepository users = new(() => Now);
        InMemoryLocalGroupRepository groups = new();
        EventBus = new InMemoryEventBus();
        LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
        users.CreateUser(_alice, "Alice", AppAuthority.User, group.Id, credential: LocalPasswordHasher.Create("hunter2", iterations: 100));
        _sessions = new LocalSessionService(
            users, seeder, EventBus, new BoundedAuditLog(maxEntries: 100),
            InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => Now);

        Manager = new InMemoryProcessManager(Clock, _sessions, EventBus);
        Log = new LogFileWriter(Clock, new BoundedDiagnosticSink(maxEntries: 1000), EventBus);

        // ADR 0077/0080: kazowebd, a system app of the host as in the Ecosystem build.
        AppManifest kazowebd = AppDefinitionMapper.ToManifest(new Kazo.Os.Apps.WebServer.KazoWebdApp());
        // ADR 0124: two sample projects of their own assemblies, a command and a service, run in workers.
        AppManifest sampleCommand = AppDefinitionMapper.ToManifest(new Kazo.Os.Samples.TerminalApp.TerminalSampleApp());
        AppManifest sampleService = AppDefinitionMapper.ToManifest(new Kazo.Os.Samples.ServiceApp.ServiceSampleApp());
        AppManifest[] commands = [.. BuiltInCommands.Select(AppDefinitionMapper.ToManifest), .. TestCommands.Manifests, kazowebd, sampleCommand, sampleService];
        AppManifest host = HostManifest();
        Catalog = AppCatalog.Build([host, .. commands], [kazowebd.Id]).Catalog!;
        AppDiscoveryResult discovery = AppEntryPointDiscovery.Discover(
            AppCatalog.Build(commands).Catalog!,
            new Dictionary<string, Assembly>(StringComparer.Ordinal)
            {
                ["Kazo.Os.Commands.dll"] = typeof(Kazo.Os.Commands.Cd.CdCommand).Assembly,
                [TestCommands.AssemblyFile] = typeof(OsFixture).Assembly,
                ["Kazo.Os.Apps.WebServer.dll"] = typeof(Kazo.Os.Apps.WebServer.KazoWebdApp).Assembly,
                ["Kazo.Os.Samples.TerminalApp.dll"] = typeof(Kazo.Os.Samples.TerminalApp.TerminalSampleApp).Assembly,
                ["Kazo.Os.Samples.ServiceApp.dll"] = typeof(Kazo.Os.Samples.ServiceApp.ServiceSampleApp).Assembly
            });
        Assert.True(discovery.IsSuccess, string.Join("; ", discovery.Errors.Select(error => error.ToString())));
        Dictionary<string, AppDescriptor> descriptors = new(discovery.Descriptors!, StringComparer.Ordinal)
        {
            [HostAppId] = new AppDescriptor(host, typeof(object), typeof(object).Assembly)
        };

        AppEnablementRegistry enablement = new(Catalog);
        // ADR 0074: the local network stack, as the Ecosystem host composes it.
        InMemorySimulatedNetworkService network = new(SmokeTestNetworkSeed.Hosts, SmokeTestWebsites());
        Ports = new ListeningPortTable(
            pid => Manager.TryGetActive(pid, out _),
            () => network.GetHost("127.0.0.1") is { } localhost
                ? localhost.Ports.Where(static port => port.State == SimulatedPortState.Open).Select(static port => port.PortNumber)
                : []);
        Resolver = new KazoDnsResolver(
            network, fileSystem, Clock, () => Catalog.Manifests.Values.Where(manifest => enablement.IsEnabled(manifest.Id)),
            new HostedSiteNameSource(fileSystem, Clock));
        LocalNetworkServices localNetwork = new(Ports, Resolver, new WebAppActivator(() => Orchestrator!, Ports));
        AppExecutionContextFactory contextFactory = new(
            _grants, fileSystem, EventBus, topicBus, new InMemoryNotificationQueue(maxEntriesPerUser: 20),
            Diagnostics, Clock, Manager,
            intentDispatcherProvider: () => Dispatcher!,
            logWriter: Log,
            network: network,
            appCatalog: new AppCatalogGatewayServices(Catalog, enablement),
            shellHostProvider: () => ShellHost!,
            metrics: new SystemMetricsServices(Drives: new AppDriveGateway(() => router, VirtualHardwareProfile.Default))
            {
                // ADR 0146: KazoCrossOriginIsolation reads what a test puts in Isolation.
                CrossOriginIsolation = new Kazo.Os.Platform.Core.Isolation.AppCrossOriginIsolationGateway(() => Isolation.Status)
            },
            localNetwork: localNetwork)
        {
            // ADR 0128: KazoBackground tasks as child processes, in a worker when the fixture has some.
            BackgroundJobs = () => BackgroundJobs!
        };

        if (workers)
        {
            Runtime = new LoopbackWebWorkerRuntime(hardwareConcurrency, workerStartupDelay, loaderFactory, synchronousCalls);
            Pool = new WorkerPool(
                wrapRuntime?.Invoke(Runtime) ?? Runtime,
                poolOptions ?? FastOptions,
                Clock,
                () => Manager,
                (message, pid) =>
                {
                    Syslog.Enqueue(pid is { } id ? $"[{id}] {message}" : message);
                    Log.Write(LogFileTarget.Syslog, Kazo.Os.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Warning, "kernel", message, pid);
                },
                Startup);
        }

        RemoteHost = Pool is null ? null : new WorkerRemoteProcessHost(Pool);
        Orchestrator = new AppLifecycleOrchestrator(
            Catalog, descriptors, enablement, Manager, _grants, contextFactory, EventBus,
            elevationTickets: new InMemoryElevationTicketStore(),
            remoteHost: RemoteHost);
        BackgroundJobs = new BackgroundJobService(
            Manager,
            appId => Orchestrator.TryGetDescriptor(appId, out AppDescriptor? descriptor) ? descriptor : null,
            () => RemoteHost,
            maxConcurrentJobsPerApp: 2);
        Interpreter = new ShellInterpreter(Orchestrator, Catalog, enablement);
        ShellHost = new ShellHostService(Interpreter, Manager, new RefusingElevationService());
        Dispatcher = new AppIntentDispatcher(
            Orchestrator, Catalog, enablement,
            new FileAssociationResolver(Catalog, enablement, new OsSettingsService(fileSystem, _sessions, Clock), fileSystem),
            _grants, fileSystem);
    }

    /// <summary>Timings shortened for tests; same rules as the ADR defaults.</summary>
    public static WorkerPoolOptions FastOptions { get; } = new()
    {
        HeartbeatPeriod = TimeSpan.FromMilliseconds(20),
        UnresponsiveThreshold = TimeSpan.FromMilliseconds(400),
        BusyThreshold = TimeSpan.FromMilliseconds(150),
        KillAcknowledgeTimeout = TimeSpan.FromMilliseconds(300),
        OverflowIdleTimeout = TimeSpan.FromMilliseconds(400),
        IdleWaitTimeout = TimeSpan.FromMilliseconds(10),
        VirtualCores = 1,
        MeterCpu = false
    };

    /// <summary>Every <c>[AppManifest]</c> class of <c>Kazo.Os.Commands</c>: all the built-in commands.</summary>
    public static IReadOnlyList<AppDefinition> BuiltInCommands { get; } =
    [
        .. typeof(Kazo.Os.Commands.Cd.CdApp).Assembly.GetTypes()
            .Where(static type => type.GetCustomAttribute<AppManifestAttribute>() is not null)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .Select(static type => (AppDefinition)Activator.CreateInstance(type)!)
    ];

    public ManualSimulationClock Clock { get; }

    public InMemoryProcessManager Manager { get; }

    public InMemoryEventBus EventBus { get; }

    /// <summary>Gets the app lane of the OS's events (ADR 0038), which workers reach by message (ADR 0124).</summary>
    public InMemoryTopicMessageBus TopicBus { get; }

    public IFileSystemService FileSystem { get; }

    public LogFileWriter Log { get; }

    /// <summary>Gets every app's diagnostic entries (what <c>/var/log/apps/&lt;appId&gt;.log</c> receives).</summary>
    public BoundedDiagnosticSink Diagnostics { get; } = new(maxEntries: 1000);

    public AppCatalog Catalog { get; }

    public AppLifecycleOrchestrator Orchestrator { get; }

    public ShellInterpreter Interpreter { get; }

    public ShellHostService ShellHost { get; }

    public AppIntentDispatcher Dispatcher { get; }

    public LoopbackWebWorkerRuntime? Runtime { get; }

    public WorkerPool? Pool { get; }

    /// <summary>Gets the out-of-thread host of the fixture, when it has workers.</summary>
    public WorkerRemoteProcessHost? RemoteHost { get; }

    /// <summary>Gets the background tasks of the OS (ADR 0128), two at a time per app and user.</summary>
    public BackgroundJobService BackgroundJobs { get; }

    /// <summary>Gets the table of listening ports (ADR 0074).</summary>
    public ListeningPortTable Ports { get; }

    /// <summary>Gets the resolver kazodns.</summary>
    public KazoDnsResolver Resolver { get; }

    public SystemStartupSignal Startup { get; } = new();

    /// <summary>Gets the lines the worker pool wrote to <c>/var/log/syslog</c>, prefixed with their PID.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Syslog { get; } = new();

    public AuthenticatedPrincipal Principal { get; private set; } = default!;

    public IAppExecutionContext Host { get; private set; } = default!;

    public ShellSession Session { get; private set; } = default!;

    /// <summary>Builds the OS, logs Alice in and opens a terminal with its shell.</summary>
    public static async Task<OsFixture> CreateAsync(
        bool workers,
        WorkerPoolOptions? poolOptions = null,
        int hardwareConcurrency = 4,
        TimeSpan? workerStartupDelay = null,
        Func<Kazo.Os.Platform.WorkerHost.Worker.IWorkerAssemblyLoader>? loaderFactory = null,
        bool synchronousCalls = false,
        Func<IWebWorkerRuntime, IWebWorkerRuntime>? wrapRuntime = null)
    {
        OsFixture fixture = new(workers, poolOptions, hardwareConcurrency, workerStartupDelay, loaderFactory, synchronousCalls, wrapRuntime);
        fixture.Principal = await fixture._sessions.LoginAsync(fixture._alice, "hunter2");
        foreach (string capability in HostCapabilities)
        {
            fixture.Grant(HostAppId, capability);
        }

        // Every command gets what it declares, as the system-app seeder grants it (ADR 0048).
        foreach (AppManifest command in fixture.Catalog.Manifests.Values.Where(static manifest => manifest.Kind != AppKind.Window))
        {
            foreach (string capability in command.Capabilities)
            {
                fixture.Grant(command.Id, capability);
            }
        }

        AppLaunchResult launched = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(HostAppId, fixture.Principal, []));
        Assert.True(launched.IsSuccess, launched.ErrorMessage);
        fixture.Host = launched.Context!;
        fixture.Session = new ShellSession(fixture.Host, fixture.Principal);
        return fixture;
    }

    public void Grant(string appId, string capability) =>
        _grants.Grant(appId, Principal.UserId.ToString(), capability, CapabilityGrantSource.SystemPolicy, AppAuthority.Administrator);

    /// <summary>Opens another shell on the same terminal (a second tab), to act while the first one is busy.</summary>
    public ShellSession OpenShell() => new(Host, Principal);

    /// <summary>Runs one command line in the terminal's shell, capturing its output.</summary>
    public Task<ShellRun> RunAsync(string line, string? input = null, CancellationToken cancellationToken = default) =>
        RunAsync(Session, line, input, cancellationToken);

    /// <summary>Runs one command line in <paramref name="session"/>, capturing its output.</summary>
    public async Task<ShellRun> RunAsync(ShellSession session, string line, string? input = null, CancellationToken cancellationToken = default)
    {
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        ShellExecutionResult result = await Interpreter.ExecuteAsync(
            session,
            line,
            new ShellExecutionOptions
            {
                StandardInput = input is null ? null : new StringReader(input),
                StandardOutput = stdout,
                StandardError = stderr
            },
            cancellationToken);
        return new ShellRun(result.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a line while advancing simulated time, for <c>sleep</c> and other clock waits.</summary>
    public async Task<ShellRun> RunWithClockAsync(string line, int maxTicks = 50)
    {
        Task<ShellRun> run = RunAsync(line);
        for (int tick = 0; tick < maxTicks && !run.IsCompleted; tick++)
        {
            await Task.WhenAny(run, Task.Delay(30));
            if (!run.IsCompleted)
            {
                Clock.Advance();
            }
        }

        return await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task<string> ReadFileAsync(string path)
    {
        using IDisposable scope = KazoExecutionContext.Enter(Host);
        return await KazoFile.ReadAllTextAsync(path);
    }

    public async Task WriteFileAsync(string path, string content)
    {
        using IDisposable scope = KazoExecutionContext.Enter(Host);
        string? directory = KazoPath.GetDirectoryName(path);
        if (directory is not null)
        {
            await KazoDirectory.CreateDirectoryAsync(directory);
        }

        await KazoFile.WriteAllTextAsync(path, content);
    }

    /// <summary>Lists every file and directory under <paramref name="root"/> with its content, for comparing two runs.</summary>
    public async Task<string> SnapshotTreeAsync(string root)
    {
        using IDisposable scope = KazoExecutionContext.Enter(Host);
        List<string> lines = [];
        await WalkAsync(root);
        return string.Join('\n', lines);

        async Task WalkAsync(string directory)
        {
            foreach (string entry in (await KazoDirectory.GetFileSystemEntriesAsync(directory)).Order(StringComparer.Ordinal))
            {
                if (await KazoDirectory.ExistsAsync(entry))
                {
                    lines.Add($"{entry}/");
                    await WalkAsync(entry);
                }
                else
                {
                    UnixFileMode mode = await KazoFile.GetUnixFileModeAsync(entry);
                    byte[] content = await KazoFile.ReadAllBytesAsync(entry);
                    lines.Add($"{entry} {Convert.ToString((int)mode, 8)} {Convert.ToBase64String(content)}");
                }
            }
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, polling.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting{(because is null ? string.Empty : ": " + because)}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Gets the log lines of one app.</summary>
    public Task<string> ReadAppLogAsync(string appId) =>
        Task.FromResult(string.Join('\n', Diagnostics.Entries.Where(entry => entry.Category == appId).Select(static entry => entry.Message)));

    /// <summary>Gets the PID of the running process of <paramref name="appId"/>.</summary>
    public long? FindPid(string appId) =>
        Manager.GetActiveProcesses().LastOrDefault(process => process.AppId == appId)?.Pid.Value;

    public async ValueTask DisposeAsync()
    {
        TestCommands.ReleaseSpinners();
        if (Pool is not null)
        {
            await Pool.DisposeAsync();
        }

        await Log.DisposeAsync();
    }

    private static ISimulatedWebsiteRegistry SmokeTestWebsites()
    {
        InMemorySimulatedWebsiteRegistry registry = new();
        foreach (ISimulatedWebsiteController controller in SmokeTestNetworkSeed.Websites)
        {
            registry.Register(controller);
        }

        return registry;
    }

    private static AppManifest HostManifest() => new()
    {
        Id = HostAppId,
        Name = "Terminal",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "Hosts the shell.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.WorkerHost.Tests", "org.kazoos.terminal.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = HostCapabilities
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>No administrator here: sudo always fails, identically in both modes.</summary>
    private sealed class RefusingElevationService : IElevationService
    {
        public Task<ElevationRequestResult> RequestSudoAsync(string adminLoginName, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElevationRequestResult(false, null, null, "elevation.denied"));

        public Task<ElevationRequestResult> RequestCapabilityApprovalAsync(
            string adminLoginName, string password, string capability, VirtualPath? path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElevationRequestResult(false, null, null, "elevation.denied"));
    }
}

/// <summary>The outcome of one command line.</summary>
internal sealed record ShellRun(int ExitCode, string Stdout, string Stderr);
