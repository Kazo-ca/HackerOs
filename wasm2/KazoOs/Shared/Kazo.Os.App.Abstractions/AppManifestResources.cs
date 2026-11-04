namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Declares the build-time-estimated simulation resource weights an app requests, mirroring the
/// runtime <c>ResourceProfile</c> shape defined by ADR 0012 in <c>Kazo.Os.Simulation.Abstractions</c>.
/// This manifest-level record intentionally duplicates that shape rather than referencing it,
/// because <c>Kazo.Os.App.Abstractions</c> has no dependency on <c>Kazo.Os.Simulation.Abstractions</c>;
/// trusted platform code maps this declaration onto the runtime profile at app-descriptor build time.
/// Field values are unvalidated here — <see cref="AppManifestValidator"/> enforces the <c>[0, 1]</c>
/// range and burst-at-least-baseline invariant so invalid manifests surface as ordinary validation
/// errors instead of constructor exceptions.
/// </summary>
/// <param name="BaselineCpuWeight">Steady-state CPU weight in <c>[0, 1]</c>.</param>
/// <param name="BurstCpuWeight">Peak CPU weight in <c>[0, 1]</c>.</param>
/// <param name="BaselineMemoryWeight">Steady-state memory weight in <c>[0, 1]</c>.</param>
/// <param name="BurstMemoryWeight">Peak memory weight in <c>[0, 1]</c>.</param>
/// <param name="BaselineStorageIoWeight">Steady-state storage I/O weight in <c>[0, 1]</c>.</param>
/// <param name="BurstStorageIoWeight">Peak storage I/O weight in <c>[0, 1]</c>.</param>
/// <param name="BaselineNetworkIoWeight">Steady-state network I/O weight in <c>[0, 1]</c>.</param>
/// <param name="BurstNetworkIoWeight">Peak network I/O weight in <c>[0, 1]</c>.</param>
public sealed record AppResourceProfileManifest(
    double BaselineCpuWeight,
    double BurstCpuWeight,
    double BaselineMemoryWeight,
    double BurstMemoryWeight,
    double BaselineStorageIoWeight,
    double BurstStorageIoWeight,
    double BaselineNetworkIoWeight,
    double BurstNetworkIoWeight)
{
    /// <summary>Gets the zero-weight profile used by apps that decline to estimate resource usage.</summary>
    public static AppResourceProfileManifest None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}
