using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Simulation.Abstractions.Processes;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>ADR 0068: a network operation in flight holds socket buffers charged to the app's process.</summary>
public sealed partial class Wave4NetworkTests
{
    [Fact]
    public async Task A_network_call_charges_socket_buffers_to_the_process_only_while_in_flight()
    {
        SocketLedger ledger = new();
        AppNetworkGateway gateway = new(
            _network, new GrantAllCapabilityChecker(), "org.kazoos.cmd.ping", pid: ProcessId.FromInt64(9), kernelMemory: ledger);

        await gateway.ResolveAsync("hackersearch.net");

        Assert.Equal([(9L, KernelMemoryKind.Socket, AppNetworkGateway.SocketBufferBytes)], ledger.Opened);
        Assert.Equal(0, ledger.Held);
    }

    [Fact]
    public async Task A_gateway_without_a_tracking_ledger_charges_nothing()
    {
        AppNetworkGateway gateway = new(_network, new GrantAllCapabilityChecker(), "org.kazoos.cmd.ping", pid: ProcessId.FromInt64(9));

        await gateway.ResolveAsync("hackersearch.net");
    }

    private sealed class SocketLedger : IKernelMemoryLedger
    {
        public List<(long Pid, KernelMemoryKind Kind, long Bytes)> Opened { get; } = [];

        public long Held { get; private set; }

        public bool IsTracking => true;

        public KernelMemoryCharge Charge(ProcessId pid, KernelMemoryKind kind, long bytes = 0)
        {
            Opened.Add((pid.Value, kind, bytes));
            return new KernelMemoryCharge(delta => Held += delta, bytes);
        }
    }
}
