using System.Collections.Immutable;
using Kazo.Os.Platform.Core.Network.Websites;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// Seeds the default simulated network with hosts matching the legacy
/// <c>src/core/network.ts</c> topology. Each host record is immutable and
/// deterministic; no real network or OS data is ever consulted.
/// </summary>
public static class DefaultSimulatedHostCatalog
{
    /// <summary>
    /// Returns the default set of <see cref="SimulatedHost"/> entries.
    /// </summary>
    public static IReadOnlyList<SimulatedHost> Build() =>
    [
        // localhost: its open ports are the system daemons of this machine (sshd, mysqld), listed
        // by netstat without a process. HTTP ports belong to the apps that listen (ADR 0074).
        new SimulatedHost(
            Ip:          "127.0.0.1",
            Hostname:    "localhost",
            IsUp:        true,
            LatencyMs:   0.1,
            Ports:
            [
                Port(22,   SimulatedPortState.Open,     "ssh",        "OpenSSH 8.2p1",     "SSH protocol 2.0"),
                Port(3306, SimulatedPortState.Open,     "mysql",      "MySQL 5.7.30",       "MySQL database server"),
                Port(25,   SimulatedPortState.Filtered, "smtp"),
                Port(443,  SimulatedPortState.Filtered, "https"),
            ],
            OsFingerprint: new("Linux", "5.4.0", 99)),

        // example.com
        new SimulatedHost(
            Ip:          "192.168.1.10",
            Hostname:    "example.com",
            IsUp:        true,
            LatencyMs:   15,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "Apache httpd 2.4.41", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https", "Apache httpd 2.4.41", "HTTP server"),
                Port(22,  SimulatedPortState.Filtered, "ssh"),
                Port(25,  SimulatedPortState.Filtered, "smtp"),
                Port(110, SimulatedPortState.Filtered, "pop3"),
            ],
            OsFingerprint: new("Linux", "Ubuntu", 85)),

        // hackersearch.net
        new SimulatedHost(
            Ip:          "192.168.1.90",
            Hostname:    "hackersearch.net",
            IsUp:        true,
            LatencyMs:   10,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "nginx 1.20.0", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https", "nginx 1.20.0", "HTTP server"),
            ],
            OsFingerprint: new("Linux", "Debian", 80)),

        // hackmail.com
        new SimulatedHost(
            Ip:          "192.168.1.50",
            Hostname:    "hackmail.com",
            IsUp:        true,
            LatencyMs:   18,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "Apache httpd 2.4.51", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https", "Apache httpd 2.4.51", "HTTP server"),
                Port(25,  SimulatedPortState.Filtered, "smtp"),
                Port(993, SimulatedPortState.Filtered, "imaps"),
            ],
            OsFingerprint: new("Linux", "Ubuntu", 82)),

        // cryptobank.com
        new SimulatedHost(
            Ip:          "192.168.1.60",
            Hostname:    "cryptobank.com",
            IsUp:        true,
            LatencyMs:   22,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "Apache httpd 2.4.41", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https", "Apache httpd 2.4.41", "HTTP server"),
            ],
            OsFingerprint: new("Linux", "CentOS", 78)),

        // mybank.net
        new SimulatedHost(
            Ip:          "192.168.1.20",
            Hostname:    "mybank.net",
            IsUp:        true,
            LatencyMs:   25,
            Ports:
            [
                Port(80,   SimulatedPortState.Open,     "http",       "Apache httpd 2.4.41", "HTTP server"),
                Port(443,  SimulatedPortState.Open,     "https",      "Apache httpd 2.4.41", "HTTP server"),
                Port(8443, SimulatedPortState.Open,     "https-alt",  "nginx 1.18.0",         "Proxy server"),
                Port(21,   SimulatedPortState.Filtered, "ftp"),
                Port(22,   SimulatedPortState.Filtered, "ssh"),
                Port(23,   SimulatedPortState.Filtered, "telnet"),
                Port(3306, SimulatedPortState.Filtered, "mysql"),
            ],
            OsFingerprint: new("Unix", "CentOS", 75)),

        // targetbank.com
        new SimulatedHost(
            Ip:          "192.168.1.30",
            Hostname:    "targetbank.com",
            IsUp:        true,
            LatencyMs:   30,
            Ports:
            [
                Port(80,   SimulatedPortState.Open,     "http",       "Apache httpd 2.4.41", "HTTP server"),
                Port(443,  SimulatedPortState.Open,     "https",      "Apache httpd 2.4.41", "HTTP server"),
                Port(8080, SimulatedPortState.Open,     "http-proxy", "nginx 1.18.0",         "Proxy server"),
                Port(21,   SimulatedPortState.Open,     "ftp",        "vsftpd 3.0.3",         "FTP server"),
                Port(22,   SimulatedPortState.Open,     "ssh",        "OpenSSH 8.2p1",         "SSH protocol 2.0"),
                Port(3306, SimulatedPortState.Open,     "mysql",      "MySQL 5.7.30",           "MySQL database server"),
                Port(23,   SimulatedPortState.Filtered, "telnet"),
                Port(25,   SimulatedPortState.Filtered, "smtp"),
                Port(110,  SimulatedPortState.Filtered, "pop3"),
                Port(8443, SimulatedPortState.Filtered, "https-alt"),
            ],
            OsFingerprint: new("Linux", "Debian", 90)),

        // router.local
        new SimulatedHost(
            Ip:          "192.168.1.1",
            Hostname:    "router.local",
            IsUp:        true,
            LatencyMs:   2,
            Ports:
            [
                Port(53,  SimulatedPortState.Open,     "domain", "BIND 9.16.1",     "DNS server"),
                Port(80,  SimulatedPortState.Open,     "http",   "lighttpd 1.4.55", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https",  "lighttpd 1.4.55", "HTTP server"),
                Port(22,  SimulatedPortState.Filtered, "ssh"),
                Port(23,  SimulatedPortState.Filtered, "telnet"),
                Port(25,  SimulatedPortState.Filtered, "smtp"),
            ],
            OsFingerprint: new("Router", "DD-WRT", 95)),

        // darknet.market
        new SimulatedHost(
            Ip:          "192.168.1.70",
            Hostname:    "darknet.market",
            IsUp:        true,
            LatencyMs:   120,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "Tor hidden service", null),
                Port(443, SimulatedPortState.Open,     "https", "Tor hidden service", null),
            ],
            OsFingerprint: new("Unknown", "Unknown", 10)),

        // hackerz.forum
        new SimulatedHost(
            Ip:          "192.168.1.80",
            Hostname:    "hackerz.forum",
            IsUp:        true,
            LatencyMs:   35,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "nginx 1.18.0", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https", "nginx 1.18.0", "HTTP server"),
            ],
            OsFingerprint: new("Linux", "Debian", 70)),

        // techcorp.com
        new SimulatedHost(
            Ip:          "192.168.1.40",
            Hostname:    "techcorp.com",
            IsUp:        true,
            LatencyMs:   20,
            Ports:
            [
                Port(80,  SimulatedPortState.Open,     "http",  "IIS 10.0", "HTTP server"),
                Port(443, SimulatedPortState.Open,     "https", "IIS 10.0", "HTTP server"),
                Port(3389,SimulatedPortState.Filtered, "ms-wbt-server"),
            ],
            OsFingerprint: new("Windows", "Server 2019", 88)),

        // hackersearch.net (second alias host)
        new SimulatedHost(
            Ip:          "192.168.1.91",
            Hostname:    "hackerz-search.net",
            IsUp:        false,
            LatencyMs:   0,
            Ports: [],
            OsFingerprint: null),
    ];

    /// <summary>
    /// Returns the website controllers that serve the default hosts: KazoSearch (the Browser's
    /// home page and search engine), KazoMail, CryptoBank, the darknet market and the hacker forum.
    /// A new instance per call, so every network composed from it has its own sessions.
    /// </summary>
    public static IReadOnlyList<ISimulatedWebsiteController> BuildWebsites() =>
    [
        new KazoSearchController(),
        new KazoMailController(),
        new CryptoBankController(),
        new DarknetMarketController(),
        new KazoForumController(),
    ];

    // ── Port factory helpers ───────────────────────────────────────────────

    private static SimulatedPort Port(
        int number, SimulatedPortState state,
        string serviceName, string? serviceVersion = null, string? serviceInfo = null) =>
        new(number, state, new SimulatedPortService(serviceName, serviceVersion, serviceInfo));
}
