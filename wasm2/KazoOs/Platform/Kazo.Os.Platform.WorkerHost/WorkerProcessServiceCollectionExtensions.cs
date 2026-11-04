using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Processes.Accounting;
using Kazo.Os.Platform.WorkerHost.Host;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Time;
using Kazo.Os.Simulation.Abstractions.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kazo.Os.Platform.WorkerHost;

/// <summary>
/// Composes headless processes in Web Workers (ADR 0070, 0071) for a host that has them: the
/// WebAssembly hosts call this after registering an <see cref="IWebWorkerRuntime"/>; a host that does
/// not runs every process in-process. Since ADR 0130 every Kazo OS host, the server included, serves
/// the OS in WebAssembly and has workers.
/// </summary>
public static class WorkerProcessServiceCollectionExtensions
{
    /// <summary>Category of the worker pool's lines in <c>/var/log/syslog</c>.</summary>
    public const string LogCategory = "workers";

    /// <summary>
    /// Registers the worker pool, the <see cref="IRemoteProcessHost"/> the orchestrator uses, the
    /// worker view of <c>/proc/hackeros/workers</c> and the measurement decorators. Requires an
    /// <see cref="IWebWorkerRuntime"/>, <see cref="ISimulationClock"/>, <see cref="IProcessManager"/>
    /// and <see cref="LogFileWriter"/> in the container.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configure">Adjusts the defaults (sizes, timings, preloaded assemblies).</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddHackerOsWorkerProcesses(
        this IServiceCollection services,
        Func<WorkerPoolOptions, WorkerPoolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<SystemStartupSignal>();
        services.AddScoped(provider =>
        {
            ResourceAccountingPlan? plan = provider.GetService<ResourceAccountingPlan>();
            WorkerPoolOptions options = new()
            {
                // ADR 0070, section 2: one worker per virtual core of the simulated machine.
                VirtualCores = Math.Max(1, (int)Math.Ceiling(VirtualHardwareProfile.Default.CpuCapacity)),
                // "Not responding" uses the CPU measurement's threshold (ADR 0067, section 4).
                UnresponsiveThreshold = plan?.Settings.UnresponsiveThreshold ?? ResourceAccountingSettings.DefaultUnresponsiveThreshold,
                // Workers meter their processes' CPU when the CPU is measured (ADR 0066).
                MeterCpu = plan?.CpuActive ?? false
            };
            LogFileWriter? log = provider.GetService<LogFileWriter>();
            return new WorkerPool(
                provider.GetRequiredService<IWebWorkerRuntime>(),
                configure?.Invoke(options) ?? options,
                provider.GetRequiredService<ISimulationClock>(),
                () => provider.GetService<IProcessManager>(),
                (message, pid) => log?.Write(LogFileTarget.Syslog, DiagnosticSeverity.Warning, LogCategory, message, pid),
                provider.GetRequiredService<SystemStartupSignal>());
        });
        services.AddScoped<IWorkerPoolStatus>(provider => provider.GetRequiredService<WorkerPool>());
        services.AddScoped<IRemoteProcessHost>(provider => new WorkerRemoteProcessHost(provider.GetRequiredService<WorkerPool>()));
        services.AddScoped(provider => new WorkerAwareCpuTimeAccountant(
            provider.GetRequiredService<ICpuTimeAccountant>(), provider.GetRequiredService<WorkerPool>()));
        services.AddScoped(provider => new WorkerAwareMemoryAccountant(
            provider.GetRequiredService<IMemoryAccountant>(), provider.GetRequiredService<WorkerPool>()));
        return services;
    }
}
