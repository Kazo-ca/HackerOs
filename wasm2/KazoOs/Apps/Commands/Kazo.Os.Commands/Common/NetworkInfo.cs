using System.Globalization;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Commands.Common;

/// <summary>One network interface as <c>ip</c>, <c>ifconfig</c> and <c>netstat -i</c> show it.</summary>
internal sealed record NetworkInterface(
    string Name,
    int Index,
    string Flags,
    int Mtu,
    string LinkType,
    string Mac,
    string Address,
    int PrefixLength,
    string Broadcast,
    long RxBytes,
    long RxPackets,
    long RxErrors,
    long RxDropped,
    long TxBytes,
    long TxPackets,
    long TxErrors,
    long TxDropped);

/// <summary>
/// The machine's network configuration for <c>ip</c> and <c>netstat</c> (ADR 0064). The counters
/// are the real ones of <c>/proc/net/dev</c> (ADR 0057: traffic of the apps charged to
/// <c>eth0</c>); the addressing is the simulated LAN's: <c>lo</c> 127.0.0.1/8 and <c>eth0</c>
/// 192.168.1.100/24 behind the router 192.168.1.1, with a locally administered MAC derived from
/// the host name, so each machine keeps the same one.
/// </summary>
internal static class NetworkInfo
{
    /// <summary>Address of this machine on the simulated LAN.</summary>
    public const string LocalAddress = "192.168.1.100";

    /// <summary>The LAN's router, the default gateway.</summary>
    public const string Gateway = "192.168.1.1";

    /// <summary>Reads the interfaces with their counters.</summary>
    public static async Task<List<NetworkInterface>> GetInterfacesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, long[]> counters = new(StringComparer.Ordinal);
        try
        {
            foreach (string line in await KazoFile.ReadAllLinesAsync("/proc/net/dev", cancellationToken))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || line.Contains('|'))
                {
                    continue;
                }

                long[] values = [.. line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(field => long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : 0)];
                counters[line[..colon].Trim()] = values;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without /proc access the counters stay at zero.
        }

        long Counter(string name, int index) => counters.TryGetValue(name, out long[]? values) && index < values.Length ? values[index] : 0;
        NetworkInterface Make(string name, int index, string flags, int mtu, string type, string mac, string address, int prefix, string broadcast) =>
            new(name, index, flags, mtu, type, mac, address, prefix, broadcast,
                Counter(name, 0), Counter(name, 1), Counter(name, 2), Counter(name, 3),
                Counter(name, 8), Counter(name, 9), Counter(name, 10), Counter(name, 11));

        return
        [
            Make("lo", 1, "LOOPBACK,UP,LOWER_UP", 65536, "loopback", "00:00:00:00:00:00", "127.0.0.1", 8, string.Empty),
            Make("eth0", 2, "BROADCAST,MULTICAST,UP,LOWER_UP", 1500, "ether", MacAddress(), LocalAddress, 24, "192.168.1.255")
        ];
    }

    /// <summary>A stable, locally administered MAC address (<c>02:…</c>) derived from the host name.</summary>
    public static string MacAddress()
    {
        string host = KazoExecutionContext.CurrentOrNull is null ? "hackeros" : KazoEnvironment.MachineName;
        uint hash = 2166136261;
        foreach (char ch in host)
        {
            hash = (hash ^ ch) * 16777619;
        }

        return string.Create(CultureInfo.InvariantCulture, $"02:42:c0:{(hash >> 16) & 0xFF:x2}:{(hash >> 8) & 0xFF:x2}:{hash & 0xFF:x2}");
    }

    /// <summary>The routing table: the LAN on <c>eth0</c> and the default route through the router.</summary>
    public static IReadOnlyList<(string Destination, string Gateway, string Mask, int Prefix, string Flags, string Device)> Routes { get; } =
    [
        ("0.0.0.0", Gateway, "0.0.0.0", 0, "UG", "eth0"),
        ("192.168.1.0", "0.0.0.0", "255.255.255.0", 24, "U", "eth0")
    ];
}
