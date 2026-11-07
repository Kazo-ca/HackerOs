using System.Globalization;

namespace Kazo.Os.App.Abstractions;

/// <summary>
/// One real-network port an app declares in its manifest (ADR 0235 §3): a protocol and a port
/// number, written <c>tcp/6697</c> or <c>udp/5353</c> in <c>manifest.json</c>
/// (<see cref="AppManifest.RealNetworkPorts"/>). The server's relay accepts such a port for the app
/// once <c>network.real.access</c> is approved, unless its <c>BlockedPorts</c> list refuses it.
/// </summary>
/// <param name="Protocol"><see cref="RealNetworkPorts.Tcp"/> or <see cref="RealNetworkPorts.Udp"/>.</param>
/// <param name="Port">Port number, 1 to 65535.</param>
public readonly record struct RealNetworkPort(string Protocol, int Port)
{
    /// <summary>Returns the canonical form, <c>tcp/6697</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Protocol}/{Port}");
}

/// <summary>
/// Parses and orders the <c>protocol/port</c> declarations of <see cref="AppManifest.RealNetworkPorts"/>
/// (ADR 0235 §3). Shared by the manifest validator, the approval window and the server relay, so the
/// three read the same text the same way.
/// </summary>
public static class RealNetworkPorts
{
    /// <summary>The TCP protocol name (<c>tcp</c>).</summary>
    public const string Tcp = "tcp";

    /// <summary>The UDP protocol name (<c>udp</c>).</summary>
    public const string Udp = "udp";

    /// <summary>
    /// Parses one declaration. The protocol is case-insensitive and the port a plain decimal number:
    /// no range, no sign, no leading zero, no white space.
    /// </summary>
    /// <param name="value">Text such as <c>tcp/6697</c>.</param>
    /// <param name="port">The parsed port, with a lower-case protocol.</param>
    /// <param name="error">Why <paramref name="value"/> is refused, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse(string? value, out RealNetworkPort port, out string? error)
    {
        port = default;
        if (string.IsNullOrEmpty(value))
        {
            error = "A real-network port cannot be empty; write it as 'tcp/6697' or 'udp/5353'.";
            return false;
        }

        int slash = value.IndexOf('/', StringComparison.Ordinal);
        string protocol = slash < 0 ? string.Empty : value[..slash].ToLowerInvariant();
        string number = slash < 0 ? string.Empty : value[(slash + 1)..];
        if (protocol is not (Tcp or Udp))
        {
            error = $"Real-network port '{value}' must start with 'tcp/' or 'udp/'.";
            return false;
        }

        if (number.Length is 0 or > 5
            || number[0] == '0'
            || !number.All(char.IsAsciiDigit)
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed > 65535)
        {
            error = $"Real-network port '{value}' must end with a port number from 1 to 65535.";
            return false;
        }

        port = new RealNetworkPort(protocol, parsed);
        error = null;
        return true;
    }

    /// <summary>
    /// Parses every declaration and drops the invalid ones, in canonical order (TCP first, then by
    /// port) without duplicates. Used where the manifest was already validated and only the valid
    /// entries matter (approval window, server relay).
    /// </summary>
    /// <param name="values">Declarations, or <see langword="null"/>.</param>
    public static IReadOnlyList<RealNetworkPort> ParseValid(IEnumerable<string>? values) =>
        values is null
            ? []
            : [.. values
                .Select(static value => TryParse(value, out RealNetworkPort port, out _) ? port : (RealNetworkPort?)null)
                .OfType<RealNetworkPort>()
                .Distinct()
                .OrderBy(static port => port.Protocol, StringComparer.Ordinal)
                .ThenBy(static port => port.Port)];

    /// <summary>
    /// Returns the canonical text of the valid declarations (<c>tcp/6667</c>, <c>tcp/6697</c>…), or
    /// <see langword="null"/> when there is none, as <c>manifest.json</c> omits the property then.
    /// </summary>
    /// <param name="values">Declarations, or <see langword="null"/>.</param>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? values)
    {
        IReadOnlyList<RealNetworkPort> ports = ParseValid(values);
        return ports.Count == 0 ? null : [.. ports.Select(static port => port.ToString())];
    }
}
