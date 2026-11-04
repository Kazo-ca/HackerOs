using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Network.Local;

/// <summary>
/// The local half of the OS network stack (ADR 0074), shared by every network gateway of a session:
/// the table of listening ports, the resolver <c>hackdns</c> and on-demand activation of web apps.
/// </summary>
/// <param name="Ports">Listening ports.</param>
/// <param name="Resolver">The resolver.</param>
/// <param name="Activator">On-demand activation; <see langword="null"/> leaves web apps to be started by hand.</param>
public sealed record LocalNetworkServices(ListeningPortTable Ports, KazoDnsResolver Resolver, WebAppActivator? Activator = null);

/// <summary>
/// Who calls the local network stack through one <see cref="AppNetworkGateway"/>: what the OS needs
/// to own a listening socket (<c>netstat -p</c>), to decide whether the caller may see the owner of
/// another one, and to start a web app on demand as the caller's user.
/// </summary>
/// <param name="Services">The session's local network stack.</param>
/// <param name="Manifest">Manifest of the calling app: its <c>web.urls</c> may be listened on without <c>network.listen</c>.</param>
/// <param name="Principal">User the calling process runs as.</param>
/// <param name="IsRootElevated">Whether the call runs elevated to root through <c>sudo</c> (sees every owner).</param>
public sealed record LocalNetworkCaller(
    LocalNetworkServices Services,
    AppManifest Manifest,
    AuthenticatedPrincipal Principal,
    bool IsRootElevated = false)
{
    /// <summary>Gets whether the caller acts as root: the <c>root</c> login, the system authority, or <c>sudo</c>.</summary>
    public bool IsRoot =>
        IsRootElevated
        || Principal.Authority == AppAuthority.System
        || string.Equals(Principal.LoginName.Value, "root", StringComparison.Ordinal);
}
