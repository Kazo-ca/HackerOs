using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The host switch of the process limits of ADR 0125 (<c>HackerOs:ResourceLimits</c> in
/// <c>appsettings.json</c>).
/// </summary>
/// <param name="Enabled">
/// Whether limits apply. <see langword="false"/> turns every one of them off: no <c>nproc</c>,
/// <c>nofile</c>, CPU or memory limit, <c>/proc/&lt;pid&gt;/limits</c> reads <c>unlimited</c> and
/// <c>ulimit</c>/<c>prlimit</c> answer "Operation not permitted" to any change.
/// </param>
public sealed record ResourceLimitsSettings(bool Enabled)
{
    /// <summary>Limits on, the default of every host.</summary>
    public static ResourceLimitsSettings Default { get; } = new(true);
}

/// <summary>Reads <see cref="ResourceLimitsSettings"/> from the host configuration (ADR 0125, section 6).</summary>
/// <remarks>
/// Each host calls <see cref="AddHackerOsResourceLimits"/> before
/// <see cref="EcosystemServiceCollectionExtensions.AddHackerOsEcosystem"/>; a host or a test that does
/// not gets <see cref="ResourceLimitsSettings.Default"/>.
/// </remarks>
public static class ResourceLimitsServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="ReadSettings"/>.</summary>
    public const string SectionName = "HackerOs:ResourceLimits";

    /// <summary>Reads <c>HackerOs:ResourceLimits:Enabled</c>; absent or unreadable reads as <see langword="true"/>.</summary>
    /// <param name="configuration">The host configuration; <see langword="null"/> reads as an absent section.</param>
    /// <returns>The settings.</returns>
    public static ResourceLimitsSettings ReadSettings(IConfiguration? configuration)
    {
        string? enabled = configuration?.GetSection(SectionName)["Enabled"];
        return bool.TryParse(enabled, out bool value) ? new ResourceLimitsSettings(value) : ResourceLimitsSettings.Default;
    }

    /// <summary>Registers the host's <see cref="ResourceLimitsSettings"/>.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration (<c>appsettings.json</c>).</param>
    /// <returns>The settings, also registered as a singleton.</returns>
    public static ResourceLimitsSettings AddHackerOsResourceLimits(this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ResourceLimitsSettings settings = ReadSettings(configuration);
        services.AddSingleton(settings);
        return settings;
    }
}
