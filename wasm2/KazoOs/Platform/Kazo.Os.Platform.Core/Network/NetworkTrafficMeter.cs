using System.Text;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// Charges the traffic of one app instance to <c>eth0</c>, or to <c>lo</c> for traffic that stays on
/// this machine (ADR 0074), in <c>/proc/net/dev</c>, and to its process (<c>rchar</c>/<c>wchar</c>
/// of <c>/proc/&lt;pid&gt;/io</c>), per ADR 0057 sections 3 and 5.
/// </summary>
/// <remarks>
/// <para>
/// A "packet" is one request or one response, as ADR 0057 defines it. Its size is what that
/// exchange would weigh on the wire: the application payload the gateway really built or received
/// (HTTP request line, headers and form body; status line, headers and body) plus the IPv4 and
/// transport headers in front of it (20 + 20 bytes for TCP, 20 + 8 for UDP and ICMP). DNS and ping
/// follow RFC 1035 and the 56-byte default payload of <c>ping</c>.
/// </para>
/// <para>
/// A failure (host not found, unreachable, timed out, refused) counts the request as sent and one
/// receive error, the column ADR 0057 reserves for them.
/// </para>
/// </remarks>
internal sealed class NetworkTrafficMeter(
    INetworkInterfaceStatistics? statistics,
    IProcessIoAccounting? processIo,
    ProcessId? pid)
{
    private const int IpHeader = 20;
    private const int TcpHeader = 20;
    private const int UdpHeader = 8;
    private const int IcmpHeader = 8;
    private const int PingPayload = 56;
    private const int DnsHeader = 12;
    private const int DnsAnswerFixed = 12; // name pointer (2) + type/class/TTL/length (10)

    /// <summary>Gets whether anything records traffic; when not, callers skip the size estimates.</summary>
    public bool IsEnabled => statistics is not null || (processIo is not null && pid is not null);

    /// <summary>Records one DNS query and its answer (or a receive error when it failed).</summary>
    public void Dns(string name, int answers, bool succeeded, bool local = false)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Question: QNAME (length-prefixed labels, ~name + 2) + QTYPE + QCLASS.
        int question = DnsHeader + name.Length + 2 + 4;
        Transmit(IpHeader + UdpHeader + question, local);
        if (succeeded)
        {
            Receive(IpHeader + UdpHeader + question + (answers * (DnsAnswerFixed + 4)), local);
        }
        else
        {
            ReceiveError(local);
        }
    }

    /// <summary>Records one ICMP echo request and its reply.</summary>
    public void Echo(bool replied, bool local = false)
    {
        if (!IsEnabled)
        {
            return;
        }

        const int packet = IpHeader + IcmpHeader + PingPayload;
        Transmit(packet, local);
        if (replied)
        {
            Receive(packet, local);
        }
        else
        {
            ReceiveError(local);
        }
    }

    /// <summary>Records one HTTP exchange, every redirect hop included.</summary>
    public void Http(HackHttpRequest request, HackNetworkResult<HackHttpResponse> result, bool local = false)
    {
        if (!IsEnabled)
        {
            return;
        }

        int requestSize = RequestSize(request);
        if (!result.Succeeded)
        {
            Transmit(requestSize, local);
            ReceiveError(local);
            return;
        }

        HackHttpResponse response = result.Value!;
        // Each redirect is a full round trip: the same request, and a header-only 3xx answer.
        for (int hop = 0; hop < response.RedirectCount; hop++)
        {
            Transmit(requestSize, local);
            Receive(IpHeader + TcpHeader + 64, local);
        }

        Transmit(requestSize, local);
        Receive(ResponseSize(response), local);
    }

    /// <summary>Records a TCP probe: one SYN per port, one SYN-ACK or RST per port that answered.</summary>
    public void Probe(HackNetworkResult<IReadOnlyList<HackPortProbeResult>> result, int portCount, bool local = false)
    {
        if (!IsEnabled)
        {
            return;
        }

        const int segment = IpHeader + TcpHeader;
        if (!result.Succeeded)
        {
            Transmit(segment, local);
            ReceiveError(local);
            return;
        }

        foreach (HackPortProbeResult port in result.Value!)
        {
            Transmit(segment, local);
            if (port.State != HackPortState.Filtered)
            {
                Receive(segment, local);
            }
        }

        // Ports the probe did not report (none today) still cost their SYN.
        for (int missing = result.Value!.Count; missing < portCount; missing++)
        {
            Transmit(segment, local);
        }
    }

    /// <summary>Records OS detection: the sixteen TCP/UDP/ICMP probes of <c>nmap -O</c>, answered when the host is up.</summary>
    public void Fingerprint(bool answered, bool local = false)
    {
        if (!IsEnabled)
        {
            return;
        }

        const int probes = 16;
        const int segment = IpHeader + TcpHeader;
        for (int probe = 0; probe < probes; probe++)
        {
            Transmit(segment, local);
            if (answered)
            {
                Receive(segment, local);
            }
        }

        if (!answered)
        {
            ReceiveError(local);
        }
    }

    /// <summary>
    /// Records one TCP segment or UDP datagram of an open socket (ADR 0102): the payload plus the
    /// IPv4 and transport headers.
    /// </summary>
    /// <param name="payload">Payload bytes.</param>
    /// <param name="outbound">Sent (<see langword="true"/>) or received.</param>
    /// <param name="udp">UDP (8-byte header) rather than TCP (20 bytes).</param>
    public void Segment(int payload, bool outbound, bool udp)
    {
        if (!IsEnabled)
        {
            return;
        }

        int size = IpHeader + (udp ? UdpHeader : TcpHeader) + payload;
        if (outbound)
        {
            Transmit(size, local: false);
        }
        else
        {
            Receive(size, local: false);
        }
    }

    /// <summary>Records a connect that failed: the SYN (or first datagram) sent and one receive error.</summary>
    /// <param name="udp">UDP rather than TCP.</param>
    public void ConnectFailed(bool udp)
    {
        if (!IsEnabled)
        {
            return;
        }

        Transmit(IpHeader + (udp ? UdpHeader : TcpHeader), local: false);
        ReceiveError(local: false);
    }

    private static int RequestSize(HackHttpRequest request)
    {
        StringBuilder head = new();
        head.Append(request.Method).Append(' ').Append(request.RequestUri.PathAndQuery).Append(" HTTP/1.1\r\n");
        head.Append("Host: ").Append(request.RequestUri.Authority).Append("\r\n");
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        int body = 0;
        if (request.Content is { } content)
        {
            // ADR 0074 section 1: the body travels as is, announced by its two headers.
            head.Append("Content-Type: ").Append(content.ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(content.Length).Append("\r\n");
            body = content.Length;
        }

        if (request.Cookies.Count > 0)
        {
            head.Append("Cookie: ").AppendJoin("; ", request.Cookies.Select(static cookie => $"{cookie.Name}={cookie.Value}")).Append("\r\n");
        }

        head.Append("\r\n");
        return IpHeader + TcpHeader + Encoding.UTF8.GetByteCount(head.ToString()) + body;
    }

    private static int ResponseSize(HackHttpResponse response)
    {
        StringBuilder head = new();
        head.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append("\r\n");
        foreach (KeyValuePair<string, string> header in response.Headers)
        {
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        head.Append("\r\n");
        return IpHeader + TcpHeader + Encoding.UTF8.GetByteCount(head.ToString()) + response.ContentBytes.Length;
    }

    /// <summary>The interface a packet crosses: <c>lo</c> when both ends are this machine.</summary>
    private static string Device(bool local) => local ? NetworkInterfaceNames.Loopback : NetworkInterfaceNames.Ethernet;

    private void Transmit(int bytes, bool local)
    {
        statistics?.RecordTransmit(Device(local), bytes);
        if (processIo is not null && pid is { } owner)
        {
            processIo.RecordWrite(owner, bytes, toStorage: false);
        }
    }

    private void Receive(int bytes, bool local)
    {
        statistics?.RecordReceive(Device(local), bytes);
        if (processIo is not null && pid is { } owner)
        {
            processIo.RecordRead(owner, bytes, fromStorage: false);
        }
    }

    private void ReceiveError(bool local) => statistics?.RecordReceiveError(Device(local));
}
