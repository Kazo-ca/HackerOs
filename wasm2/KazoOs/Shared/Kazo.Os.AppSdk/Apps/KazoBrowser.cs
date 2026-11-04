namespace Kazo.Os.AppSdk.Apps;

/// <summary>
/// Opens web pages in the Kazo OS Browser (ASP.Hack phase, ADR 0078 §3), as
/// <c>xdg-open</c> or <c>Process.Start(url)</c> hand a URL to the default browser.
/// </summary>
/// <remarks>
/// Both methods start a new Browser window through <see cref="KazoApps.LaunchAsync"/> and need
/// <c>apps.launch</c>. Every member targets the app running the current code (ambient context of
/// ADR 0043).
/// </remarks>
public static class KazoBrowser
{
    /// <summary>App ID of the Kazo OS Browser.</summary>
    public const string AppId = "org.hackeros.browser";

    /// <summary>Launch argument that makes the Browser show its page in a detached window.</summary>
    public const string DetachedArgument = "--detached";

    /// <summary>Opens <paramref name="url"/> in a new Browser window.</summary>
    /// <param name="url">An address as typed in the address bar: a URL (<c>https://site.hack/</c>, <c>file:///home/…</c>) or a path.</param>
    /// <param name="cancellationToken">Cancels the launch.</param>
    /// <exception cref="ArgumentException"><paramref name="url"/> is empty.</exception>
    /// <exception cref="KazoAppLaunchException">The Browser is not installed or failed to start.</exception>
    /// <exception cref="Kazo.Os.AppSdk.IO.KazoUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return KazoApps.LaunchAsync(AppId, [url], cancellationToken);
    }

    /// <summary>
    /// Opens <paramref name="url"/> in a detached window of the host browser, outside the OS
    /// desktop and driven by it (ADR 0078). The Browser window that serves the page also opens, with
    /// a placeholder and a button to bring the page back. Call it from a user gesture (a click): the
    /// host browser blocks windows opened without one, and the page then stays in the OS Browser
    /// with a notification explaining how to allow pop-ups.
    /// </summary>
    /// <param name="url">An address as typed in the address bar.</param>
    /// <param name="cancellationToken">Cancels the launch.</param>
    /// <exception cref="ArgumentException"><paramref name="url"/> is empty.</exception>
    /// <exception cref="KazoAppLaunchException">The Browser is not installed or failed to start.</exception>
    /// <exception cref="Kazo.Os.AppSdk.IO.KazoUnauthorizedAccessException">This app lacks <c>apps.launch</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task OpenDetachedAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return KazoApps.LaunchAsync(AppId, [DetachedArgument, url], cancellationToken);
    }
}
