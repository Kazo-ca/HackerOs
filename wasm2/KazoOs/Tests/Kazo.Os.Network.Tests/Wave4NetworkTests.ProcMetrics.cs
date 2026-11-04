using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Core.Metrics;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0057 sections 3 and 5: the app network gateway counts each app's real traffic in
/// <c>/proc/net/dev</c> (<c>eth0</c>) and in the <c>rchar</c>/<c>wchar</c> of its process.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    private static readonly ProcessId MeteredPid = ProcessId.FromInt64(5);

    private AppNetworkGateway MeteredGateway(
        NetworkInterfaceStatistics statistics,
        ProcessIoAccounting io,
        params string[] denied) =>
        new(_network, new GrantAllCapabilityChecker(denied), "org.tests.metered", _serverConnection, _proxy, statistics, io, MeteredPid);

    [Fact]
    public async Task Gateway_counts_a_resolution_as_one_packet_each_way_on_eth0()
    {
        NetworkInterfaceStatistics statistics = new();
        ProcessIoAccounting io = new();

        await MeteredGateway(statistics, io).ResolveAsync("hackersearch.net");

        NetworkInterfaceCounters eth0 = statistics.GetInterfaces()[1];
        Assert.Equal(NetworkInterfaceNames.Ethernet, eth0.Name);
        Assert.Equal(1, eth0.TransmitPackets);
        Assert.Equal(1, eth0.ReceivePackets);
        Assert.True(eth0.ReceiveBytes > eth0.TransmitBytes, "The answer repeats the question and adds a record.");
        Assert.Equal(new NetworkInterfaceCounters(NetworkInterfaceNames.Loopback, 0, 0, 0, 0, 0, 0, 0, 0), statistics.GetInterfaces()[0]);

        ProcessIoCounters process = io.Get(MeteredPid);
        Assert.Equal(eth0.TransmitBytes, process.WrittenChars);
        Assert.Equal(eth0.ReceiveBytes, process.ReadChars);
        Assert.Equal(0, process.ReadBytes); // network bytes never reach storage
    }

    [Fact]
    public async Task Gateway_counts_unknown_hosts_and_unreachable_pings_as_receive_errors()
    {
        NetworkInterfaceStatistics statistics = new();
        AppNetworkGateway gateway = MeteredGateway(statistics, new ProcessIoAccounting());

        await gateway.ResolveAsync("unknown-host-12345.com");
        await gateway.PingAsync("hackerz-search.net", TimeSpan.FromSeconds(1));

        NetworkInterfaceCounters eth0 = statistics.GetInterfaces()[1];
        Assert.Equal(2, eth0.TransmitPackets);
        Assert.Equal(0, eth0.ReceivePackets);
        Assert.Equal(2, eth0.ReceiveErrors);
    }

    [Fact]
    public async Task Gateway_counts_the_http_body_it_received()
    {
        NetworkInterfaceStatistics statistics = new();
        ProcessIoAccounting io = new();

        HackNetworkResult<HackHttpResponse> response = await MeteredGateway(statistics, io).SendAsync(
            new HackHttpRequest("GET", new Uri("https://hackersearch.net/"), [], null, []));

        Assert.True(response.Succeeded);
        NetworkInterfaceCounters eth0 = statistics.GetInterfaces()[1];
        Assert.True(eth0.ReceiveBytes > response.Value!.Content.Length);
        Assert.Equal(1 + response.Value.RedirectCount, eth0.ReceivePackets);
        Assert.Equal(eth0.ReceiveBytes, io.Get(MeteredPid).ReadChars);
    }

    [Fact]
    public async Task Gateway_counts_nothing_for_a_call_refused_by_capability()
    {
        NetworkInterfaceStatistics statistics = new();
        ProcessIoAccounting io = new();

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(async () =>
            await MeteredGateway(statistics, io, AppCapabilities.NetworkSimulatedRead).ResolveAsync("hackersearch.net"));

        Assert.Equal(0, statistics.GetInterfaces()[1].TransmitPackets);
        Assert.Equal(ProcessIoCounters.Zero, io.Get(MeteredPid));
    }
}
