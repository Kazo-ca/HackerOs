using Kazo.Os.Simulation.Abstractions.Metrics;

namespace Kazo.Os.Platform.Core.Metrics;

/// <summary>
/// In-memory <see cref="INetworkInterfaceStatistics"/> with the two interfaces of ADR 0057
/// section 5: <c>lo</c> (traffic between two processes of this machine, ADR 0074) and <c>eth0</c>. Traffic recorded for an
/// unknown interface name is ignored, as a packet for a missing device would be dropped.
/// </summary>
public sealed class NetworkInterfaceStatistics : INetworkInterfaceStatistics
{
    private static readonly string[] InterfaceOrder = [NetworkInterfaceNames.Loopback, NetworkInterfaceNames.Ethernet];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, NetworkInterfaceCounters> _interfaces = new(StringComparer.Ordinal)
    {
        [NetworkInterfaceNames.Loopback] = new(NetworkInterfaceNames.Loopback, 0, 0, 0, 0, 0, 0, 0, 0),
        [NetworkInterfaceNames.Ethernet] = new(NetworkInterfaceNames.Ethernet, 0, 0, 0, 0, 0, 0, 0, 0)
    };

    /// <inheritdoc />
    public void RecordTransmit(string interfaceName, long bytes) =>
        Update(interfaceName, counters => counters with
        {
            TransmitBytes = counters.TransmitBytes + Math.Max(0, bytes),
            TransmitPackets = counters.TransmitPackets + 1
        });

    /// <inheritdoc />
    public void RecordReceive(string interfaceName, long bytes) =>
        Update(interfaceName, counters => counters with
        {
            ReceiveBytes = counters.ReceiveBytes + Math.Max(0, bytes),
            ReceivePackets = counters.ReceivePackets + 1
        });

    /// <inheritdoc />
    public void RecordReceiveError(string interfaceName) =>
        Update(interfaceName, counters => counters with { ReceiveErrors = counters.ReceiveErrors + 1 });

    /// <inheritdoc />
    public IReadOnlyList<NetworkInterfaceCounters> GetInterfaces()
    {
        lock (_gate)
        {
            return [.. InterfaceOrder.Select(name => _interfaces[name])];
        }
    }

    private void Update(string interfaceName, Func<NetworkInterfaceCounters, NetworkInterfaceCounters> change)
    {
        lock (_gate)
        {
            if (_interfaces.TryGetValue(interfaceName, out NetworkInterfaceCounters counters))
            {
                _interfaces[interfaceName] = change(counters);
            }
        }
    }
}
