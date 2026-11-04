using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Ip;

/// <summary>
/// <c>ip [-4] [-br] [-s] {addr|link|route|neigh} [show]</c> (ADR 0064), iproute2 style, over
/// <see cref="NetworkInfo"/>: interfaces <c>lo</c> and <c>eth0</c> with their addresses and the
/// <c>/proc/net/dev</c> counters (<c>-s</c>), the routing table, and the neighbour (ARP) table
/// with the LAN's router. Called as <c>ifconfig</c> it prints the net-tools layout.
/// </summary>
public sealed class IpCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<NetworkInterface> interfaces = await NetworkInfo.GetInterfacesAsync(cancellationToken);
        IReadOnlyList<string> argv = context.App.Environment.GetCommandLineArgs();
        if (argv.Count > 0 && argv[0] == "ifconfig")
        {
            return await IfconfigAsync(context, interfaces, context.Arguments.FirstOrDefault(a => !a.StartsWith('-')));
        }

        bool brief = false, stats = false;
        List<string> words = [];
        foreach (string arg in context.Arguments)
        {
            switch (arg)
            {
                case "-br" or "-brief" or "--brief": brief = true; break;
                case "-s" or "-stats" or "-statistics": stats = true; break;
                case "-4" or "-6" or "-c" or "-color" or "--color": break;
                default: words.Add(arg); break;
            }
        }

        string objectName = words.Count > 0 ? words[0] : string.Empty;
        string? device = words.SkipWhile(w => w != "dev").Skip(1).FirstOrDefault();
        IEnumerable<NetworkInterface> selected = interfaces.Where(nic => device is null || nic.Name == device);
        switch (objectName)
        {
            case "a" or "addr" or "address":
                foreach (NetworkInterface nic in selected)
                {
                    if (brief)
                    {
                        await context.StandardOutput.WriteLineAsync($"{nic.Name,-16} {"UP",-14} {nic.Address}/{nic.PrefixLength}");
                        continue;
                    }

                    await WriteLinkAsync(context, nic, stats);
                    string scope = nic.Name == "lo" ? "host" : "global";
                    string broadcast = nic.Broadcast.Length > 0 ? $" brd {nic.Broadcast}" : string.Empty;
                    await context.StandardOutput.WriteLineAsync($"    inet {nic.Address}/{nic.PrefixLength}{broadcast} scope {scope} {(nic.Name == "lo" ? "lo" : "dynamic eth0")}");
                    await context.StandardOutput.WriteLineAsync("       valid_lft forever preferred_lft forever");
                }

                return 0;
            case "l" or "link":
                foreach (NetworkInterface nic in selected)
                {
                    if (brief)
                    {
                        await context.StandardOutput.WriteLineAsync($"{nic.Name,-16} {"UP",-14} {nic.Mac} <{nic.Flags}>");
                        continue;
                    }

                    await WriteLinkAsync(context, nic, stats);
                }

                return 0;
            case "r" or "ro" or "route":
                await context.StandardOutput.WriteLineAsync($"default via {NetworkInfo.Gateway} dev eth0 proto dhcp src {NetworkInfo.LocalAddress} metric 100");
                await context.StandardOutput.WriteLineAsync($"192.168.1.0/24 dev eth0 proto kernel scope link src {NetworkInfo.LocalAddress} metric 100");
                return 0;
            case "n" or "neigh" or "neighbor" or "neighbour":
                await context.StandardOutput.WriteLineAsync($"{NetworkInfo.Gateway} dev eth0 lladdr 02:42:c0:a8:01:01 REACHABLE");
                return 0;
            case "":
                await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["Usage"]);
                await context.StandardError.WriteLineAsync("where  OBJECT := { address | link | route | neigh }");
                await context.StandardError.WriteLineAsync("       OPTIONS := { -s[tatistics] | -br[ief] | -4 }");
                return 255;
            default:
                await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["UnknownObject", objectName]);
                return 1;
        }
    }

    private static async Task WriteLinkAsync(TerminalExecutionContext context, NetworkInterface nic, bool stats)
    {
        string qdisc = nic.Name == "lo" ? "noqueue" : "fq_codel";
        await context.StandardOutput.WriteLineAsync($"{nic.Index}: {nic.Name}: <{nic.Flags}> mtu {nic.Mtu} qdisc {qdisc} state {(nic.Name == "lo" ? "UNKNOWN" : "UP")} group default qlen 1000");
        string broadcast = nic.Name == "lo" ? "00:00:00:00:00:00" : "ff:ff:ff:ff:ff:ff";
        await context.StandardOutput.WriteLineAsync($"    link/{nic.LinkType} {nic.Mac} brd {broadcast}");
        if (stats)
        {
            await context.StandardOutput.WriteLineAsync("    RX:  bytes packets errors dropped  missed   mcast");
            await context.StandardOutput.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"    {nic.RxBytes,10} {nic.RxPackets,7} {nic.RxErrors,6} {nic.RxDropped,7} {0,7} {0,7}"));
            await context.StandardOutput.WriteLineAsync("    TX:  bytes packets errors dropped carrier collsns");
            await context.StandardOutput.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"    {nic.TxBytes,10} {nic.TxPackets,7} {nic.TxErrors,6} {nic.TxDropped,7} {0,7} {0,7}"));
        }
    }

    private static async Task<int> IfconfigAsync(TerminalExecutionContext context, List<NetworkInterface> interfaces, string? only)
    {
        foreach (NetworkInterface nic in interfaces.Where(n => only is null || n.Name == only))
        {
            string flags = nic.Name == "lo" ? "73<UP,LOOPBACK,RUNNING>" : "4163<UP,BROADCAST,RUNNING,MULTICAST>";
            await context.StandardOutput.WriteLineAsync($"{nic.Name}: flags={flags}  mtu {nic.Mtu}");
            string mask = nic.PrefixLength == 8 ? "255.0.0.0" : "255.255.255.0";
            string broadcast = nic.Broadcast.Length > 0 ? $"  broadcast {nic.Broadcast}" : string.Empty;
            await context.StandardOutput.WriteLineAsync($"        inet {nic.Address}  netmask {mask}{broadcast}");
            await context.StandardOutput.WriteLineAsync(nic.Name == "lo"
                ? "        loop  txqueuelen 1000  (Local Loopback)"
                : $"        ether {nic.Mac}  txqueuelen 1000  (Ethernet)");
            await context.StandardOutput.WriteLineAsync($"        RX packets {nic.RxPackets}  bytes {nic.RxBytes} ({CommandIO.HumanSize(nic.RxBytes)}B)");
            await context.StandardOutput.WriteLineAsync($"        RX errors {nic.RxErrors}  dropped {nic.RxDropped}  overruns 0  frame 0");
            await context.StandardOutput.WriteLineAsync($"        TX packets {nic.TxPackets}  bytes {nic.TxBytes} ({CommandIO.HumanSize(nic.TxBytes)}B)");
            await context.StandardOutput.WriteLineAsync($"        TX errors {nic.TxErrors}  dropped {nic.TxDropped} overruns 0  carrier 0  collisions 0");
            await context.StandardOutput.WriteLineAsync();
        }

        return 0;
    }
}
