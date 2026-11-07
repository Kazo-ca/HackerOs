using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Formats the real-network ports an app declares (ADR 0235 §3) for the approval of
/// <c>network.real.access</c>: the secure-desktop window and the terminal's text mode show the
/// same list, so the administrator sees which real ports the relay will open for the app.
/// </summary>
public static class RealNetworkPortText
{
    /// <summary>
    /// Returns the declared ports as <c>TCP 6667, TCP 6697</c>, or <see langword="null"/> when the
    /// app declares none or <paramref name="capability"/> is not <c>network.real.access</c>.
    /// </summary>
    /// <param name="manifest">The app asking for approval.</param>
    /// <param name="capability">The capability the line belongs to.</param>
    public static string? For(AppManifest manifest, string capability)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(capability, AppCapabilities.NetworkRealAccess, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<RealNetworkPort> ports = RealNetworkPorts.ParseValid(manifest.RealNetworkPorts);
        return ports.Count == 0
            ? null
            : string.Join(", ", ports.Select(static port => $"{port.Protocol.ToUpperInvariant()} {port.Port}"));
    }
}
