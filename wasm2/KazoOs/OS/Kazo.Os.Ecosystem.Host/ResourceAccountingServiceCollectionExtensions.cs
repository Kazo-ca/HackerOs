using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Processes.Accounting;
using Kazo.Os.Simulation.Abstractions.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
#if HACKEROS_RESOURCE_ACCOUNTING
using Kazo.Os.Platform.ResourceAccounting;
using Kazo.Os.Platform.ResourceAccounting.Memory;
#endif

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Composes the optional resource accounting of ADR 0066: reads the host's
/// <c>HackerOs:ResourceAccounting</c> section once, combines it with what the build compiled in,
/// and installs the common context-switch hook only when at least one measurement is active.
/// </summary>
/// <remarks>
/// Each host calls <see cref="AddHackerOsResourceAccounting(IServiceCollection, IConfiguration?, IReadOnlyDictionary{string, AppImage}?)"/>
/// before <see cref="EcosystemServiceCollectionExtensions.AddHackerOsEcosystem"/>. A host (or a test)
/// that never calls it gets the null accountants and <see cref="ResourceAccountingPlan.Disabled"/>:
/// exactly the simulated behavior of phases 1 to 4.
/// </remarks>
public static class ResourceAccountingServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="ReadSettings"/>.</summary>
    public const string SectionName = "HackerOs:ResourceAccounting";

    /// <summary>
    /// Gets the measurements compiled into this host: none when the build left
    /// <c>Kazo.Os.Platform.ResourceAccounting</c> out, otherwise its runtime feature switches.
    /// </summary>
    public static ResourceAccountingBuildSupport CompiledSupport =>
#if HACKEROS_RESOURCE_ACCOUNTING
        new(ResourceAccountingFeatures.IsCpuAccountingSupported, ResourceAccountingFeatures.IsMemoryAccountingSupported);
#else
        ResourceAccountingBuildSupport.None;
#endif

    /// <summary>
    /// Reads <c>HackerOs:ResourceAccounting</c> from the host configuration and composes the
    /// accounting it asks for, within what the build allows.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration (<c>appsettings.json</c>); <see langword="null"/> reads as an absent section.</param>
    /// <param name="images">
    /// The image of each app for the memory measurement (ADR 0068), usually
    /// <see cref="HostAppCatalog.Images"/>; <see langword="null"/> gives no app an image layer.
    /// </param>
    /// <returns>The resolved plan, also registered as a singleton.</returns>
    public static ResourceAccountingPlan AddHackerOsResourceAccounting(
        this IServiceCollection services, IConfiguration? configuration, IReadOnlyDictionary<string, AppImage>? images = null) =>
        services.AddHackerOsResourceAccounting(ReadSettings(configuration), CompiledSupport, hookInstaller: null, images);

    /// <summary>
    /// Composes the accounting for explicit settings and build support. Tests use it to cover every
    /// combination without touching the process-wide hook.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settings">What <c>appsettings.json</c> asks.</param>
    /// <param name="build">What the build compiled in.</param>
    /// <param name="hookInstaller">
    /// Installs the context-switch hook with the given observer and returns its installation;
    /// <see langword="null"/> uses the real hook on <c>HackExecutionContext</c>. Never called when no
    /// measurement is active.
    /// </param>
    /// <param name="images">
    /// The image of each app for the memory measurement (ADR 0068), usually
    /// <see cref="HostAppCatalog.Images"/>; <see langword="null"/> gives no app an image layer.
    /// </param>
    /// <returns>The resolved plan, also registered as a singleton.</returns>
    /// <exception cref="InvalidOperationException">A measurement is active but this assembly was built without the accounting project.</exception>
    public static ResourceAccountingPlan AddHackerOsResourceAccounting(
        this IServiceCollection services,
        ResourceAccountingSettings settings,
        ResourceAccountingBuildSupport build,
        Func<IExecutionContextSwitchObserver, IDisposable>? hookInstaller,
        IReadOnlyDictionary<string, AppImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        ResourceAccountingPlan plan = ResourceAccountingPlan.Resolve(build, settings);
        services.AddSingleton(plan);

        if (plan.RequiresContextSwitchHook)
        {
#if HACKEROS_RESOURCE_ACCOUNTING
            // Installed now, at composition: no app code has run, so no scope is open. The CPU and
            // memory measurements (ADR 0067, 0068) register themselves on this dispatcher.
            ContextSwitchDispatcher dispatcher = new();
            IDisposable installation = (hookInstaller ?? ContextSwitchHook.Install)(dispatcher);
            services.AddSingleton(dispatcher);
            services.AddSingleton(new ResourceAccountingHookInstallation(installation));
            if (plan.CpuActive && ResourceAccountingFeatures.IsCpuAccountingSupported)
            {
                // ADR 0067: utime/stime per process, compositor and idle, long tasks and "Not
                // responding". Registered before any app code runs, so it sees every switch.
                MeasuredCpuTimeAccountant cpu = new(plan.Settings.UnresponsiveThreshold);
                services.AddSingleton(new CpuAccountingRegistration(dispatcher.Register(cpu)));
                services.AddSingleton(cpu);
                services.AddSingleton<ICpuTimeAccountant>(cpu);
            }

            if (plan.MemoryActive && ResourceAccountingFeatures.IsMemoryAccountingSupported)
            {
                AddMeasuredMemory(services, dispatcher, images ?? new Dictionary<string, AppImage>(StringComparer.Ordinal));
            }
#else
            throw new InvalidOperationException(
                "A resource measurement is active but Kazo.Os.Platform.ResourceAccounting is not built into this host.");
#endif
        }

        // A measurement that is off reads the simulator, and /proc says "simulated". Registered
        // measurements above win over these.
        services.TryAddSingleton<ICpuTimeAccountant>(NullCpuTimeAccountant.Instance);
        services.TryAddSingleton<IMemoryAccountant>(NullMemoryAccountant.Instance);
        services.TryAddSingleton<IKernelMemoryLedger>(NullKernelMemoryLedger.Instance);
        return plan;
    }

#if HACKEROS_RESOURCE_ACCOUNTING
    /// <summary>
    /// Composes the measured memory of ADR 0068: the allocation layer registered on the hook now, so
    /// it counts from the first switch, the kernel-memory ledger the OS services charge, and the
    /// accountant <c>/proc</c> reads.
    /// </summary>
    private static void AddMeasuredMemory(
        IServiceCollection services,
        ContextSwitchDispatcher dispatcher,
        IReadOnlyDictionary<string, AppImage> images)
    {
        IManagedHeapProbe probe = GcManagedHeapProbe.Instance;
        AllocationTracker allocations = new(probe);
        KernelMemoryLedger kernel = new();
        MeasuredMemoryAccountant accountant = new(allocations, kernel, images, probe);
        services.AddSingleton(new MemoryAccountingRegistration(dispatcher.Register(allocations)));
        services.AddSingleton(allocations);
        services.AddSingleton(kernel);
        services.AddSingleton<IKernelMemoryLedger>(kernel);
        services.AddSingleton<IMemoryAccountant>(accountant);
    }
#endif

    /// <summary>
    /// Reads the <c>HackerOs:ResourceAccounting</c> section. A missing value takes its default
    /// (measurements on, five-second threshold); an unreadable one also does, with a warning line.
    /// </summary>
    /// <param name="configuration">The host configuration, or <see langword="null"/>.</param>
    /// <returns>The settings.</returns>
    public static ResourceAccountingSettings ReadSettings(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return ResourceAccountingSettings.Default;
        }

        IConfigurationSection section = configuration.GetSection(SectionName);
        List<string> warnings = [];
        bool cpu = ReadBoolean(section, "Cpu:Enabled", warnings);
        bool memory = ReadBoolean(section, "Memory:Enabled", warnings);
        TimeSpan threshold = ResourceAccountingSettings.DefaultUnresponsiveThreshold;
        string? thresholdText = section["Cpu:UnresponsiveThresholdMs"];
        if (thresholdText is not null)
        {
            if (int.TryParse(thresholdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int milliseconds) && milliseconds > 0)
            {
                threshold = TimeSpan.FromMilliseconds(milliseconds);
            }
            else
            {
                warnings.Add($"resource accounting: {SectionName}:Cpu:UnresponsiveThresholdMs='{thresholdText}' is not a positive integer; using {threshold.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return new ResourceAccountingSettings(cpu, memory, threshold, warnings);
    }

    private static bool ReadBoolean(IConfigurationSection section, string key, List<string> warnings)
    {
        string? text = section[key];
        if (text is null)
        {
            return true;
        }

        if (bool.TryParse(text, out bool value))
        {
            return value;
        }

        warnings.Add($"resource accounting: {SectionName}:{key}='{text}' is not true or false; using true");
        return true;
    }
}

/// <summary>
/// The installed context-switch hook, kept alive with the service provider. The OS never removes
/// it: like a kernel's scheduler hook, it lasts until the page reloads.
/// </summary>
/// <param name="Installation">The installation returned by the hook installer.</param>
public sealed record ResourceAccountingHookInstallation(IDisposable Installation);

/// <summary>
/// The memory measurement's registration on the context-switch dispatcher, kept alive with the
/// service provider like the hook itself (ADR 0068).
/// </summary>
/// <param name="Registration">The dispatcher registration of the allocation layer.</param>
public sealed record MemoryAccountingRegistration(IDisposable Registration);

/// <summary>
/// The CPU measurement's registration on the context-switch dispatcher, kept alive with the
/// service provider like the hook itself (ADR 0067).
/// </summary>
/// <param name="Registration">The dispatcher registration of <c>MeasuredCpuTimeAccountant</c>.</param>
public sealed record CpuAccountingRegistration(IDisposable Registration);
