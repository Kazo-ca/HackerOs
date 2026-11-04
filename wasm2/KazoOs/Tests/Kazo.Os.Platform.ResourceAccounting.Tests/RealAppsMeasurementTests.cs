using System.Globalization;
using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Apps.Calculator;
using Kazo.Os.Commands.Echo;
using Kazo.Os.Platform.Core.FileSystem.Virtual;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Samples.ServiceApp;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>
/// Section 5.1 of the execution-isolation plan for the CPU measurement: real apps of each family,
/// with their code unchanged, are measured once the hook is installed. Each runs through the entry
/// point the OS uses (the window's <c>SetParametersAsync</c>, the shell's scope around a command,
/// <c>ServiceAppBase.RunAsync</c>), then <c>/proc/&lt;pid&gt;/stat</c> and <c>status</c> are read
/// from the real <see cref="ProcFileSystemProvider"/>.
/// </summary>
public sealed class RealAppsMeasurementTests
{
    private const long WindowPid = 101;
    private const long CommandPid = 102;
    private const long ServicePid = 103;
    private static readonly DateTimeOffset BootUtc = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly SessionId Session = SessionId.FromGuid(Guid.NewGuid());

    [Fact]
    public async Task A_window_a_command_and_a_service_are_measured_without_changing_their_code()
    {
        MeasuredCpuTimeAccountant accountant = new(TimeSpan.FromSeconds(5));
        using (ContextSwitchHook.Install(accountant))
        {
            await RenderCalculatorAsync();
            await RunEchoAsync();
            await RunTickerServiceAsync();
        }

        ProcFileSystemProvider proc = Proc(accountant);
        foreach (long pid in new[] { WindowPid, CommandPid, ServicePid })
        {
            Assert.True(accountant.TryGetProcessTimes(ProcessId.FromInt64(pid), out ProcessCpuTimes times), $"pid {pid} measured");
            Assert.True(times.TotalTime > TimeSpan.Zero, $"pid {pid} used some CPU");
            Assert.True(times.VoluntaryContextSwitches >= 1, $"pid {pid} gave the thread back");

            string stat = await ReadAsync(proc, $"/proc/{pid}/stat");
            string[] fields = stat.TrimEnd('\n').Split(' ');
            Assert.Equal(52, fields.Length);
            Assert.Equal(pid.ToString(CultureInfo.InvariantCulture), fields[0]);
            Assert.Equal((long)(times.UserTime.TotalSeconds * 100), long.Parse(fields[13], CultureInfo.InvariantCulture));
            Assert.Equal((long)(times.SystemTime.TotalSeconds * 100), long.Parse(fields[14], CultureInfo.InvariantCulture));

            string status = await ReadAsync(proc, $"/proc/{pid}/status");
            Assert.Contains("CpuSource:\tmeasured\n", status, StringComparison.Ordinal);
            Assert.Contains("Responding:\t1\n", status, StringComparison.Ordinal);
            Assert.Contains("nonvoluntary_ctxt_switches:\t0\n", status, StringComparison.Ordinal);
        }
    }

    /// <summary>Renders the real Calculator window like the window host: its parameters set inside its own frame.</summary>
    internal static async Task RenderCalculatorAsync(long pid = WindowPid)
    {
        await using WindowRenderer renderer = new();
        Assert.NotEmpty(await renderer.RenderCalculatorAsync(pid));
    }

    /// <summary>Runs the real <c>echo</c> the way the shell interpreter does: inside the command's own scope.</summary>
    private static async Task RunEchoAsync()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new EchoApp());
        string stdout = await RunCommandAsync(new EchoCommand(manifest), CommandPid, ["measured", "by", "the", "hook"]);

        Assert.Equal("measured by the hook" + Environment.NewLine, stdout);
    }

    /// <summary>
    /// Runs a real command the way the shell interpreter does: inside the command's own scope, with
    /// <paramref name="stdin"/> as its standard input (a pipeline's previous stage).
    /// </summary>
    /// <returns>What the command wrote to its standard output.</returns>
    internal static async Task<string> RunCommandAsync(TerminalAppBase command, long pid, IReadOnlyList<string> arguments, string stdin = "")
    {
        IAppExecutionContext context = Context(command.Manifest, pid);
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        TerminalExecutionContext terminal = new(
            context, arguments, new StringReader(stdin), stdout, stderr, "/", new Dictionary<string, string>());
        int exitCode;
        using (HackExecutionContext.Enter(context))
        {
            exitCode = await command.ExecuteAsync(terminal, CancellationToken.None);
        }

        Assert.True(exitCode == 0, $"{command.Manifest.Id} exited with {exitCode}: {stderr}");
        return stdout.ToString();
    }

    /// <summary>Runs the real ticker service through <see cref="ServiceAppBase.RunAsync"/>, which opens its frame.</summary>
    private static async Task RunTickerServiceAsync()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new ServiceSampleApp());
        SampleTickerService service = new(manifest, TimeSpan.FromMilliseconds(5));
        IAppExecutionContext context = new MinimalAppExecutionContext(
            manifest,
            events: DefaultProxy.Create<IAppEventGateway>(),
            logging: new FakeAppLoggingGateway(),
            clock: new RealDelayClock(),
            processId: ProcessId.FromInt64(ServicePid),
            sessionId: Session);
        using CancellationTokenSource stop = new();
        Task run = service.RunAsync(context, stop.Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (service.TickCount < 3 && !run.IsCompleted)
        {
            await Task.Delay(5, timeout.Token);
        }

        await stop.CancelAsync();
        await run;
    }

    private static IAppExecutionContext Context(AppManifest manifest, long pid) =>
        new MinimalAppExecutionContext(manifest, processId: ProcessId.FromInt64(pid), sessionId: Session);

    private static ProcFileSystemProvider Proc(ICpuTimeAccountant accountant)
    {
        ManualSimulationClock clock = new(BootUtc, TimeSpan.FromMilliseconds(100));
        ProcessTable processes = new(
        [
            ProcessRecordFactory.Running(WindowPid, "org.hackeros.calculator", Session, BootUtc, BootUtc, kind: AppKind.Window),
            ProcessRecordFactory.Running(CommandPid, "org.hackeros.cmd.echo", Session, BootUtc, BootUtc, kind: AppKind.Terminal),
            ProcessRecordFactory.Running(ServicePid, SampleTickerService.AppId, Session, BootUtc, BootUtc, kind: AppKind.Service)
        ]);
        return new ProcFileSystemProvider(
            () => processes,
            new DeterministicResourceSimulator(clock, new SeededSimulationRandom(1), VirtualHardwareProfile.Default),
            VirtualHardwareProfile.Default,
            clock,
            metrics: new ProcMetricsSources(CpuAccountant: accountant));
    }

    private static async Task<string> ReadAsync(ProcFileSystemProvider proc, string path)
    {
        FileSystemAuthorizationContext context = new(
            new AppOperationContext
            {
                AppId = "org.hackeros.system-monitor",
                UserId = "user",
                UserName = "user",
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>([AppCapabilities.ProcessList], StringComparer.Ordinal)
            },
            [],
            BootUtc);
        FileSystemResult<FileSystemContentReadHandle> read = await proc.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)), context);
        Assert.True(read.Succeeded, $"Reading {path} failed with {read.Error?.Code}.");
        await using FileSystemContentReadHandle handle = read.Value!;
        using StreamReader reader = new(handle.Content);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// A static renderer with the services every <see cref="WindowAppBase"/> injects, for rendering
    /// real window apps outside a browser.
    /// </summary>
    internal sealed class WindowRenderer : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;

        public WindowRenderer()
        {
            ServiceCollection services = new();
            services.AddSingleton(DefaultProxy.Create<IDialogService>());
            services.AddSingleton<IFileDialogService>(provider => provider.GetRequiredService<IDialogService>());
            services.AddSingleton(DefaultProxy.Create<IWindowAppFrameworkLifecycle>());
            services.AddSingleton(DefaultProxy.Create<IAdminElevationVerifier>());
            _services = services.BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, NullLoggerFactory.Instance);
        }

        /// <summary>Renders one Calculator window of process <paramref name="pid"/> and returns its HTML.</summary>
        public Task<string> RenderCalculatorAsync(long pid)
        {
            IAppExecutionContext context = Context(AppDefinitionMapper.ToManifest(new CalculatorApp()), pid);
            return _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await _renderer.RenderComponentAsync<CalculatorWindow>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WindowAppBase.AppContext)] = context }));
                return root.ToHtmlString();
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    /// <summary>A service clock whose delays really wait, so the ticker loops on continuations.</summary>
    private sealed class RealDelayClock : IAppClockGateway
    {
        public DateTimeOffset UtcNow => BootUtc;

        public long CurrentTick => 0;

        public IDisposable Schedule(TimeSpan delay, Action callback) => throw new NotSupportedException();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => Task.Delay(delay, cancellationToken);
    }

    /// <summary>The read side of the process table that <c>/proc</c> uses.</summary>
    private sealed class ProcessTable(IReadOnlyList<ProcessRecord> active) : IProcessManager
    {
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

        public ProcessRecord Fault(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Fault) => throw new NotSupportedException();

        public Task<ProcessRecord> StopAsync(
            ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed) => throw new NotSupportedException();

        public bool TryGetSingleton(string appId, out ProcessRecord process) => throw new NotSupportedException();

        public CancellationToken GetCancellationToken(ProcessId pid) => throw new NotSupportedException();
    }

    /// <summary>A gateway or service double whose every member returns its type's default value.</summary>
    public class DefaultProxy : DispatchProxy
    {
        /// <summary>Creates a double of <typeparamref name="T"/>.</summary>
        public static T Create<T>() where T : class => Create<T, DefaultProxy>();

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Type? type = targetMethod?.ReturnType;
            return type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }
}
