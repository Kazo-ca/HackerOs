using System.Globalization;
using Kazo.Os.Simulation.Abstractions.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// The simulated hardware of a host (<c>KazoOs:Hardware</c> in <c>appsettings.json</c>): the
/// machine <c>/proc/meminfo</c> reports and the OOM killer of ADR 0126 measures against.
/// </summary>
/// <remarks>
/// Each host calls <see cref="AddKazoOsHardware"/> before
/// <see cref="EcosystemServiceCollectionExtensions.AddKazoOsEcosystem"/>; a host or a test that does
/// not gets <see cref="VirtualHardwareProfile.Default"/> (one core, 2 GiB). <c>build/verify-hosts.sh</c>
/// lowers <c>MemoryMiB</c> to drive the OOM killer in its <c>ecosystem-oom</c> run.
/// </remarks>
public static class HardwareServiceCollectionExtensions
{
    /// <summary>Configuration section read by <see cref="ReadProfile"/>.</summary>
    public const string SectionName = "KazoOs:Hardware";

    /// <summary>
    /// Reads <c>KazoOs:Hardware:MemoryMiB</c> (<c>MemTotal</c>, in MiB); absent, unreadable or not
    /// positive keeps <see cref="VirtualHardwareProfile.Default"/>.
    /// </summary>
    /// <param name="configuration">The host configuration; <see langword="null"/> reads as an absent section.</param>
    /// <returns>The profile.</returns>
    public static VirtualHardwareProfile ReadProfile(IConfiguration? configuration)
    {
        VirtualHardwareProfile profile = VirtualHardwareProfile.Default;
        string? memory = configuration?.GetSection(SectionName)["MemoryMiB"];
        return long.TryParse(memory, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mebibytes) && mebibytes > 0
            ? new VirtualHardwareProfile(
                profile.Name,
                profile.CpuCapacity,
                mebibytes * 1024d * 1024d,
                profile.StorageIoCapacityBytesPerTick,
                profile.NetworkIoCapacityBytesPerTick,
                profile.StorageCapacityBytes)
            : profile;
    }

    /// <summary>Registers the host's <see cref="VirtualHardwareProfile"/>.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration (<c>appsettings.json</c>).</param>
    /// <returns>The profile, also registered as a singleton.</returns>
    public static VirtualHardwareProfile AddKazoOsHardware(this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        VirtualHardwareProfile profile = ReadProfile(configuration);
        services.AddSingleton(profile);
        return profile;
    }
}
