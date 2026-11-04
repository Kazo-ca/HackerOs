using System.Collections.Immutable;

namespace Kazo.Os.Simulation.Abstractions.Network;

/// <summary>
/// State of a simulated port (open, closed, or filtered by a firewall).
/// </summary>
public enum SimulatedPortState
{
    /// <summary>A service is actively listening on this port.</summary>
    Open,
    /// <summary>No service is listening; connection actively refused.</summary>
    Closed,
    /// <summary>A firewall drops packets without acknowledgement.</summary>
    Filtered
}

/// <summary>
/// Immutable descriptor for one service running behind a simulated port.
/// </summary>
public sealed record SimulatedPortService(
    string Name,
    string? Version = null,
    string? Info = null);

/// <summary>
/// Immutable port entry for a simulated host.
/// </summary>
public sealed record SimulatedPort(
    int PortNumber,
    SimulatedPortState State,
    SimulatedPortService? Service = null)
{
    /// <summary>Returns true when the port is reachable by external clients.</summary>
    public bool IsReachable => State == SimulatedPortState.Open;
}

/// <summary>
/// Immutable OS fingerprint returned by scanning a simulated host.
/// </summary>
/// <param name="Name">Detected operating system name, e.g. "Linux".</param>
/// <param name="Version">Detected operating system version, e.g. a kernel range.</param>
/// <param name="Accuracy">Simulated detection accuracy percentage (0-100).</param>
public sealed record SimulatedOsFingerprint(
    string Name,
    string Version,
    int Accuracy);

/// <summary>
/// Immutable record representing a single host in the simulated network.
/// A host may be addressed by IP or by its canonical hostname.
/// </summary>
/// <param name="Ip">Dot-decimal IPv4 string, e.g. "192.168.1.10".</param>
/// <param name="Hostname">Primary fully-qualified hostname, e.g. "example.com".</param>
/// <param name="IsUp">Whether the host is currently reachable (responds to ping).</param>
/// <param name="LatencyMs">Simulated round-trip time in milliseconds.</param>
/// <param name="Ports">The ports a scan can see on the host, open or not.</param>
/// <param name="OsFingerprint">The OS a scan detects, or <see langword="null"/> when it cannot be fingerprinted.</param>
/// <param name="MacAddress">Optional MAC address (meaningful for LAN hosts only).</param>
public sealed record SimulatedHost(
    string Ip,
    string Hostname,
    bool IsUp,
    double LatencyMs,
    ImmutableArray<SimulatedPort> Ports,
    SimulatedOsFingerprint? OsFingerprint = null,
    string? MacAddress = null)
{
    /// <summary>
    /// Returns ports visible in the given inclusive port range, in port-number order.
    /// </summary>
    public IEnumerable<SimulatedPort> ScanPorts(int firstPort, int lastPort) =>
        Ports
            .Where(p => p.PortNumber >= firstPort && p.PortNumber <= lastPort)
            .OrderBy(p => p.PortNumber);
}
