using System.Text.RegularExpressions;

namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// Reads which build of the OS the page is running and which build its origin serves now
/// (ADR 0162). The browser host reads its own document and fetches the page again, bypassing caches.
/// </summary>
public interface IHostBuildProbe
{
    /// <summary>Gets the identity of the build this page booted, or <see langword="null"/> when unknown.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<string?> GetRunningBuildAsync(CancellationToken cancellationToken);

    /// <summary>Gets the identity of the build the origin serves now, or <see langword="null"/> when unreachable or unknown.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    ValueTask<string?> GetServedBuildAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Notices that the OS was updated under a running page (ADR 0162): every lazy app asset carries
/// the fingerprint of the build that booted, so after a rebuild or a deployment those files are
/// gone and an app no longer opens. Like a package manager that replaced the system's files, the
/// monitor only says a restart is needed; the host offers it.
/// </summary>
public sealed class HostBuildUpdateMonitor
{
    private readonly IHostBuildProbe _probe;
    private int _pending;

    /// <summary>Creates the monitor.</summary>
    /// <param name="probe">Reads the running and the served build.</param>
    public HostBuildUpdateMonitor(IHostBuildProbe probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>Gets whether a newer build was found: the page must restart to load it.</summary>
    public bool IsUpdatePending => Volatile.Read(ref _pending) == 1;

    /// <summary>Raised once, the first time a newer build is found.</summary>
    public event Action? UpdatePending;

    /// <summary>
    /// Compares the running build with the served one. An unreachable origin or an unreadable page
    /// is not an update: the caller keeps its own error (offline, server down).
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when the origin serves another build than the one running.</returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsUpdatePending)
        {
            return true;
        }

        string? running;
        string? served;
        try
        {
            running = await _probe.GetRunningBuildAsync(cancellationToken).ConfigureAwait(false);
            served = running is null ? null : await _probe.GetServedBuildAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }

        if (running is null || served is null || string.Equals(running, served, StringComparison.Ordinal))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            UpdatePending?.Invoke();
        }

        return true;
    }
}

/// <summary>Extracts a build identity from the markup of a Kazo OS page (ADR 0162).</summary>
public static partial class HostBuildIdentity
{
    /// <summary>
    /// Returns the fingerprinted name of <c>dotnet.js</c> found in the markup (import map or script),
    /// or <see langword="null"/>. The build inlines its boot configuration (every assembly with its
    /// fingerprint) into <c>dotnet.js</c>, so its fingerprint changes whenever any assembly does.
    /// </summary>
    /// <param name="markup">The page, or only its import map and script elements.</param>
    public static string? FromMarkup(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return null;
        }

        Match match = FingerprintedDotnetJs().Match(markup);
        return match.Success ? match.Value : null;
    }

    // Static web asset fingerprints are ten characters; the length keeps dotnet.native.js and
    // dotnet.runtime.js (import map keys) from matching.
    [GeneratedRegex(@"_framework/dotnet\.[a-z0-9]{10}\.js", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintedDotnetJs();
}
