using System.Net;
using System.Net.Sockets;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Nmap;

/// <summary>
/// Simulated <c>nmap</c> port-scanner terminal command (P4-W4-006) on the SDK network API
/// (ADR 0056, section 6): <see cref="KazoDns"/> for the address, <see cref="KazoPing"/> for host
/// discovery, <see cref="KazoTcpClient"/> for the scan and <c>-O</c>. For a host outside the
/// simulated network the OS relays a single-port probe (<c>-p &lt;port&gt;</c>) through the
/// optional server when it can (ADR 0035); a range against such a host is never relayed and the
/// host "seems down".
/// </summary>
public sealed class NmapCommand : TerminalAppBase
{
    /// <summary>Default port range when -p is not specified.</summary>
    private const int DefaultFirstPort = 1;
    private const int DefaultLastPort  = 1000;

    /// <summary>Initializes the command with its manifest.</summary>
    public NmapCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        // Human-readable lines follow the process language; the port table stays English (ADR 0104, section 5).
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        // Parse arguments: nmap [-p <range>] [-sV] [-O] <target>
        string? target = null;
        int firstPort = DefaultFirstPort;
        int lastPort  = DefaultLastPort;
        bool showVersion = false;
        bool showOs      = false;

        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-p" or "--port" && i + 1 < args.Count)
            {
                (firstPort, lastPort) = ParsePortRange(args[++i]);
            }
            else if (a is "-sV")
            {
                showVersion = true;
            }
            else if (a is "-O")
            {
                showOs = true;
            }
            else if (!a.StartsWith('-'))
            {
                target = a;
            }
        }

        if (target is null)
        {
            context.StandardError.WriteLine(text["Usage"]);
            return 1;
        }

        context.StandardOutput.WriteLine(text["Starting", DateTime.UtcNow]);

        IPAddress? address = await TryResolveAsync(target, cancellationToken).ConfigureAwait(false);
        string reportName = address is null || address.ToString() == target ? target : $"{target} ({address})";

        // Host discovery: a known host is pinged first, as nmap does without -Pn.
        KazoPingReply? discovery = null;
        if (address is not null)
        {
            discovery = await KazoPing.SendAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (discovery.Status != KazoIPStatus.Success)
            {
                context.StandardOutput.WriteLine(text["ScanReport", reportName]);
                context.StandardOutput.WriteLine(text["HostUnreachable"]);
                context.StandardOutput.WriteLine(text["DoneNoHost"]);
                return 1;
            }
        }

        IReadOnlyList<KazoPortProbeResult> ports;
        try
        {
            ports = await KazoTcpClient.ProbeAsync(target, firstPort, lastPort, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            context.StandardOutput.WriteLine(text["ScanReport", reportName]);
            context.StandardOutput.WriteLine(exception.SocketErrorCode == SocketError.HostNotFound
                ? text["HostSeemsDown"]
                : text["ProbeFailed", exception.SocketErrorCode]);
            context.StandardOutput.WriteLine(text["DoneNoHost"]);
            return 1;
        }

        context.StandardOutput.WriteLine(text["ScanReport", reportName]);
        context.StandardOutput.WriteLine(discovery is null
            ? text["HostUp"]
            : text["HostUpLatency", discovery.RoundtripTime.TotalSeconds]);

        int hidden = (lastPort - firstPort + 1) - ports.Count;
        if (hidden > 0)
        {
            context.StandardOutput.WriteLine(text["NotShown", hidden]);
        }

        if (ports.Count > 0)
        {
            context.StandardOutput.WriteLine(showVersion
                ? "PORT      STATE     SERVICE          VERSION"
                : "PORT      STATE     SERVICE");

            foreach (KazoPortProbeResult port in ports)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string portCol    = $"{port.Port}/tcp".PadRight(9);
                string stateCol   = port.State.ToString().ToLowerInvariant().PadRight(9);
                string serviceCol = (port.ServiceName ?? "unknown").PadRight(16);

                context.StandardOutput.WriteLine(showVersion && port.ServiceVersion is not null
                    ? $"{portCol} {stateCol} {serviceCol} {port.ServiceVersion}"
                    : $"{portCol} {stateCol} {serviceCol}".TrimEnd());
            }
        }

        if (showOs && address is not null
            && await KazoTcpClient.DetectOperatingSystemAsync(target, cancellationToken).ConfigureAwait(false) is { } os)
        {
            context.StandardOutput.WriteLine();
            context.StandardOutput.WriteLine(text["OsDetection"]);
            context.StandardOutput.WriteLine(text["OsDetails", os.Name, os.Version, os.Accuracy]);
        }

        double elapsedSeconds = discovery is null ? 0.5 : discovery.RoundtripTime.TotalMilliseconds * 0.1;
        context.StandardOutput.WriteLine();
        context.StandardOutput.WriteLine(text["Done", elapsedSeconds]);
        return 0;
    }

    /// <summary>Resolves <paramref name="target"/>, or returns <see langword="null"/> when no resolver knows it.</summary>
    private static async Task<IPAddress?> TryResolveAsync(string target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out IPAddress? literal))
        {
            // An address literal is only "known" when the simulated network has a host there.
            try
            {
                await KazoDns.GetHostEntryAsync(literal, cancellationToken).ConfigureAwait(false);
                return literal;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        try
        {
            IPAddress[] addresses = await KazoDns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0 ? addresses[0] : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // ── Port range parsing ────────────────────────────────────────────────

    /// <summary>
    /// Parses a port range string ("80", "1-1000", "22,80,443") into first/last bounds.
    /// Returns the defaults if the string cannot be parsed.
    /// </summary>
    private static (int First, int Last) ParsePortRange(string range)
    {
        if (range.Contains('-'))
        {
            var parts = range.Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var lo)
                && int.TryParse(parts[1], out var hi)
                && lo >= 1 && hi <= 65535 && lo <= hi)
                return (lo, hi);
        }
        else if (int.TryParse(range, out var single) && single >= 1 && single <= 65535)
        {
            return (single, single);
        }

        return (DefaultFirstPort, DefaultLastPort);
    }
}
