using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// Starts the renderer of a Blazor site for the Kazo OS browser (ADR 0084, section 3). Implemented
/// by the platform, which finds the web app that serves the page's origin, loads its assembly, checks
/// the root component and starts an auxiliary process under the tab's user; the browser renders the
/// site with <see cref="HackWebSiteView"/>.
/// </summary>
public interface IHackWebSiteHost
{
    /// <summary>Starts the renderer of the site that serves <paramref name="request"/>'s page.</summary>
    /// <param name="request">The page, its boot manifest and the browser's context.</param>
    /// <param name="cancellationToken">Cancels the wait for the site's assembly.</param>
    /// <returns>The renderer process, or why the site cannot be drawn.</returns>
    Task<HackWebSiteStart> StartAsync(HackWebSiteStartRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What the browser knows when it asks for a site's renderer.</summary>
/// <param name="PageUrl">The URL of the host page the browser received.</param>
/// <param name="Boot">The site's <c>blazor.boot.json</c>.</param>
/// <param name="Browser">The browser window's context: the renderer is its child process.</param>
public sealed record HackWebSiteStartRequest(Uri PageUrl, HackBlazorBootManifest Boot, IAppExecutionContext Browser);

/// <summary>Outcome of <see cref="IHackWebSiteHost.StartAsync"/>.</summary>
/// <param name="Process">The renderer, when the site started.</param>
/// <param name="Error">Why it did not, for the user and the developer tools console.</param>
public sealed record HackWebSiteStart(IHackWebSiteProcess? Process, string? Error)
{
    /// <summary>Gets whether the site started.</summary>
    public bool IsStarted => Process is not null;

    /// <summary>Creates a started outcome.</summary>
    public static HackWebSiteStart Started(IHackWebSiteProcess process) => new(process, null);

    /// <summary>Creates a failed outcome.</summary>
    public static HackWebSiteStart Failed(string error) => new(null, error);
}

/// <summary>
/// The renderer process of a Blazor site (ADR 0084, section 3): what the site's components run as.
/// It ends when the browser leaves the page, or when the OS stops it (task manager, <c>kill</c>).
/// </summary>
public interface IHackWebSiteProcess : IAsyncDisposable
{
    /// <summary>Gets the id of the web app.</summary>
    string AppId { get; }

    /// <summary>Gets the site's display name.</summary>
    string AppName { get; }

    /// <summary>Gets the root component type, a Blazor component of the site's assembly.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    Type RootComponent { get; }

    /// <summary>Gets the renderer's execution context: the site's app id and capabilities, the tab's user.</summary>
    IAppExecutionContext Context { get; }

    /// <summary>Gets the renderer's process id.</summary>
    ProcessId ProcessId { get; }

    /// <summary>Gets whether the process ended (stopped by the browser or by the OS).</summary>
    bool HasExited { get; }

    /// <summary>Raised once when the process ends, from outside any Blazor event.</summary>
    event Action? Exited;

    /// <summary>Ends the renderer: the tab left the page, or the site crashed (<paramref name="faulted"/>).</summary>
    /// <param name="faulted">Whether a component of the site threw.</param>
    Task StopAsync(bool faulted = false);
}
