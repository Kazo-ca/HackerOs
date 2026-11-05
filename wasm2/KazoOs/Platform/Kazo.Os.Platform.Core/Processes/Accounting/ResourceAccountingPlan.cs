namespace Kazo.Os.Platform.Core.Processes.Accounting;

/// <summary>
/// Which measurements are compiled into the running host (ADR 0066, section 2): the MSBuild
/// properties <c>KazoOsCpuAccounting</c> and <c>KazoOsMemoryAccounting</c>, as seen at run time
/// through their <c>RuntimeHostConfigurationOption</c>.
/// </summary>
/// <param name="Cpu">Whether the CPU measurement code ships in this host.</param>
/// <param name="Memory">Whether the memory measurement code ships in this host.</param>
public readonly record struct ResourceAccountingBuildSupport(bool Cpu, bool Memory)
{
    /// <summary>Gets the support of a host built with both measurements off.</summary>
    public static ResourceAccountingBuildSupport None => new(false, false);
}

/// <summary>
/// The host's <c>KazoOs:ResourceAccounting</c> section of <c>appsettings.json</c> (ADR 0066,
/// section 3). A host setting, not a setting of the simulated OS: it never lives under
/// <c>/etc/kazoos</c>.
/// </summary>
/// <param name="CpuEnabled"><c>Cpu:Enabled</c>; <see langword="true"/> when absent.</param>
/// <param name="MemoryEnabled"><c>Memory:Enabled</c>; <see langword="true"/> when absent.</param>
/// <param name="UnresponsiveThreshold">
/// <c>Cpu:UnresponsiveThresholdMs</c>: a slice longer than this marks the process "Not responding"
/// (ADR 0067, section 4). Read now, used by the CPU measurement chantier.
/// </param>
/// <param name="Warnings">One syslog line per value that could not be read and fell back to its default.</param>
public sealed record ResourceAccountingSettings(
    bool CpuEnabled,
    bool MemoryEnabled,
    TimeSpan UnresponsiveThreshold,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Default <see cref="UnresponsiveThreshold"/>: five seconds.</summary>
    public static readonly TimeSpan DefaultUnresponsiveThreshold = TimeSpan.FromMilliseconds(5000);

    /// <summary>Gets the settings of a host with no <c>KazoOs:ResourceAccounting</c> section: both on.</summary>
    public static ResourceAccountingSettings Default { get; } = new(true, true, DefaultUnresponsiveThreshold, []);
}

/// <summary>
/// The measurements a host actually runs, decided <b>once</b> at service composition from what is
/// compiled in and what <c>appsettings.json</c> asks (ADR 0066). Changing either needs a reload, like
/// a kernel boot parameter.
/// </summary>
/// <remarks>
/// A measurement compiled out always wins: <c>Enabled: true</c> is then ignored and a
/// <see cref="Notices"/> line says so in <c>/var/log/syslog</c>. When neither measurement is active,
/// <see cref="RequiresContextSwitchHook"/> is <see langword="false"/> and the ambient frame stays an
/// ordinary <see cref="AsyncLocal{T}"/>: no hook, no "enabled?" test on the hot path.
/// </remarks>
public sealed class ResourceAccountingPlan
{
    /// <summary>Syslog category of the plan's lines.</summary>
    public const string LogCategory = "resource-accounting";

    private ResourceAccountingPlan(
        ResourceAccountingBuildSupport build,
        ResourceAccountingSettings settings,
        bool cpuActive,
        bool memoryActive,
        IReadOnlyList<string> notices)
    {
        Build = build;
        Settings = settings;
        CpuActive = cpuActive;
        MemoryActive = memoryActive;
        Notices = notices;
    }

    /// <summary>Gets a plan with both measurements off, as used by headless tests and hosts that never compose accounting.</summary>
    public static ResourceAccountingPlan Disabled { get; } = Resolve(ResourceAccountingBuildSupport.None, ResourceAccountingSettings.Default with { CpuEnabled = false, MemoryEnabled = false });

    /// <summary>Gets what the host was built with.</summary>
    public ResourceAccountingBuildSupport Build { get; }

    /// <summary>Gets what <c>appsettings.json</c> asked.</summary>
    public ResourceAccountingSettings Settings { get; }

    /// <summary>Gets whether the CPU measurement runs (compiled in and enabled).</summary>
    public bool CpuActive { get; }

    /// <summary>Gets whether the memory measurement runs (compiled in and enabled).</summary>
    public bool MemoryActive { get; }

    /// <summary>Gets whether the common context-switch hook (ADR 0067, section 1) must be installed.</summary>
    public bool RequiresContextSwitchHook => CpuActive || MemoryActive;

    /// <summary>Gets the information lines to write to <c>/var/log/syslog</c> at boot.</summary>
    public IReadOnlyList<string> Notices { get; }

    /// <summary>Combines build support and settings into the plan.</summary>
    /// <param name="build">The measurements compiled into the host.</param>
    /// <param name="settings">The host settings.</param>
    /// <returns>The resolved plan, with one notice per ignored setting and a summary line.</returns>
    public static ResourceAccountingPlan Resolve(ResourceAccountingBuildSupport build, ResourceAccountingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<string> notices = [.. settings.Warnings];
        bool cpu = Decide("cpu", "KazoOsCpuAccounting", "Cpu", build.Cpu, settings.CpuEnabled, notices);
        bool memory = Decide("memory", "KazoOsMemoryAccounting", "Memory", build.Memory, settings.MemoryEnabled, notices);
        notices.Add($"resource accounting: cpu {Describe(build.Cpu, cpu)}, memory {Describe(build.Memory, memory)}");
        return new ResourceAccountingPlan(build, settings, cpu, memory, notices);
    }

    private static bool Decide(string name, string property, string section, bool built, bool enabled, List<string> notices)
    {
        if (!built && enabled)
        {
            notices.Add($"resource accounting: {name} measurement is not built into this host ({property}=false); " +
                        $"KazoOs:ResourceAccounting:{section}:Enabled=true ignored, {name} stays simulated");
        }

        return built && enabled;
    }

    private static string Describe(bool built, bool active) => active
        ? "on"
        : built ? "off (appsettings)" : "off (not built)";
}
