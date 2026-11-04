using System.Net;
using System.Net.Sockets;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Nslookup;

/// <summary>
/// <c>nslookup &lt;name|address&gt;</c> (ADR 0074, section 4): asks <c>hackdns</c>, the OS
/// resolver, through <see cref="KazoDns"/>, and prints the answer the way BIND's
/// <c>nslookup</c> does against the local stub resolver (<c>127.0.0.53</c>, as on a
/// <c>systemd-resolved</c> machine). Unlike the real tool, it also says which source answered
/// (<c>/etc/hosts</c> and its line, <c>localhost</c>, a web app's URLs, a hosted site, or the
/// simulated network), so a player can see why a name lands where it does.
/// </summary>
public sealed class NslookupCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <summary>The stub resolver's address, printed as the server.</summary>
    private const string StubResolver = "127.0.0.53";

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Messages and source comments follow the process language; the record lines stay as BIND prints them.
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string? target = context.Arguments.FirstOrDefault(static argument => !argument.StartsWith('-'));
        if (target is null)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 1;
        }

        await context.StandardOutput.WriteLineAsync($"Server:\t\t{StubResolver}");
        await context.StandardOutput.WriteLineAsync($"Address:\t{StubResolver}#53");
        await context.StandardOutput.WriteLineAsync();

        try
        {
            if (IPAddress.TryParse(target, out IPAddress? address))
            {
                KazoHostEntry reverse = await KazoDns.GetHostEntryAsync(address, cancellationToken);
                await context.StandardOutput.WriteLineAsync($"{ReverseName(address)}\tname = {reverse.HostName}.");
                return 0;
            }

            KazoHostEntry entry = await KazoDns.GetHostEntryAsync(target, cancellationToken);
            await context.StandardOutput.WriteLineAsync(SourceLine(text, entry));
            if (!string.Equals(entry.HostName, target.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {
                await context.StandardOutput.WriteLineAsync($"{target}\tcanonical name = {entry.HostName}.");
            }

            foreach (IPAddress resolved in entry.AddressList)
            {
                await context.StandardOutput.WriteLineAsync($"Name:\t{entry.HostName}");
                await context.StandardOutput.WriteLineAsync($"Address: {resolved}");
            }

            return 0;
        }
        catch (SocketException)
        {
            string name = IPAddress.TryParse(target, out IPAddress? literal) ? ReverseName(literal) : target;
            await context.StandardOutput.WriteLineAsync(text["NotFound", name]);
            await context.StandardOutput.WriteLineAsync();
            return 1;
        }
    }

    /// <summary>The line that says where the answer came from, in the comment style of <c>dig</c>.</summary>
    private static string SourceLine(IStringLocalizer<Strings> text, KazoHostEntry entry) => entry.Source switch
    {
        KazoDnsSource.HostsFile => text["Source.HostsFile", entry.SourceDetail ?? "/etc/hosts"],
        KazoDnsSource.Loopback => text["Source.Loopback"],
        KazoDnsSource.WebApp => text["Source.WebApp", entry.SourceDetail ?? string.Empty],
        KazoDnsSource.HostedSite => text["Source.HostedSite", entry.SourceDetail ?? string.Empty],
        KazoDnsSource.SimulatedNetwork => text["Source.NonAuthoritative"],
        _ => text["Source.Unknown"]
    };

    /// <summary>The PTR name of an IPv4 address: its octets reversed under <c>in-addr.arpa</c>.</summary>
    private static string ReverseName(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? string.Join('.', address.GetAddressBytes().Reverse()) + ".in-addr.arpa"
            : address.ToString();
}
